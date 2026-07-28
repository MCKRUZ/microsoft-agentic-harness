using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services.Governance;
using Domain.AI.Planner;
using Domain.AI.Skills;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Resolves and assembles tools for agent execution contexts. Supports three resolution
/// modes — Injected (all MCP tools passed through), Managed with ToolDeclarations (MCP-first
/// with keyed DI fallback), and Managed with AllowedTools (simple name-based resolution).
/// Applies plugin governance boundary filtering (AllowedTools/DeniedTools) for plugin-sourced skills.
/// </summary>
public class ToolChainBuilder : IToolChainBuilder
{
    /// <summary>
    /// Telemetry policy tag identifying a drop made by the reserved plan-capability filter, so a
    /// governance dashboard can separate this fail-open closure from ordinary policy violations.
    /// </summary>
    private const string ReservedPlanCapabilityPolicy = "reserved_plan_capability";

    private readonly ILogger<ToolChainBuilder> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IToolConverter? _toolConverter;
    private readonly IMcpToolProvider? _mcpToolProvider;

    public ToolChainBuilder(
        ILogger<ToolChainBuilder> logger,
        IServiceProvider serviceProvider,
        IToolConverter? toolConverter = null,
        IMcpToolProvider? mcpToolProvider = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _toolConverter = toolConverter;
        _mcpToolProvider = mcpToolProvider;
    }

    /// <inheritdoc />
    public Task<List<AITool>> BuildToolsAsync(SkillDefinition skill, SkillAgentOptions options, CancellationToken cancellationToken = default)
        // Public callers don't need MCP attribution — use a throwaway collector so
        // resolution paths still record where each tool came from but the result is
        // discarded.
        => BuildToolsAsync(skill, options, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken);

    private async Task<List<AITool>> BuildToolsAsync(
        SkillDefinition skill,
        SkillAgentOptions options,
        ISet<string> mcpCollector,
        CancellationToken cancellationToken = default)
    {
        var tools = new List<AITool>();

        if (skill.Mode == SkillMode.Injected && _mcpToolProvider != null)
        {
            foreach (var serverTools in await ResolveInjectedMcpToolsAsync(cancellationToken))
            {
                tools.AddRange(serverTools);
                foreach (var t in serverTools) mcpCollector.Add(t.Name);
            }

            if (options.AdditionalTools?.Count > 0)
                tools.AddRange(options.AdditionalTools);

            tools = ApplyPluginBoundaryIfPluginSkill(skill, tools);

            _logger.LogInformation(
                "Injected mode: skill {SkillId} from plugin {Plugin} received {Count} MCP tools",
                skill.Id, skill.PluginSource, tools.Count);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return FinalizeChain(tools.Where(t => seen.Add(t.Name)), DescribeSource(skill, "injected MCP tool resolution"));
        }

        if (skill.Tools?.Count > 0)
            tools.AddRange(skill.Tools);

        if (skill.ToolDeclarations?.Count > 0)
        {
            var provisionTasks = skill.ToolDeclarations.Select(d => ProvisionToolAsync(d, mcpCollector, cancellationToken));
            var results = await Task.WhenAll(provisionTasks);
            foreach (var provisioned in results)
            {
                if (provisioned != null)
                    tools.AddRange(provisioned);
            }
        }

        if (skill.AllowedTools?.Count > 0)
        {
            foreach (var toolName in skill.AllowedTools)
            {
                var resolved = ResolveToolByName(toolName);
                if (resolved != null)
                    tools.AddRange(resolved);
            }
        }

        if (options.AdditionalTools?.Count > 0)
            tools.AddRange(options.AdditionalTools);

        tools = ApplyPluginBoundaryIfPluginSkill(skill, tools);

        var seen2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return FinalizeChain(tools.Where(t => seen2.Add(t.Name)), DescribeSource(skill, "managed tool resolution"));
    }

    /// <summary>
    /// Applies the owning plugin's AllowedTools/DeniedTools boundary to <paramref name="tools"/>
    /// whenever the skill is plugin-sourced and the plugin is loaded. This runs on both the
    /// Injected and Managed resolution paths so a plugin's <c>DeniedTools</c> are enforced
    /// regardless of how the skill resolves its tools. A no-op for built-in skills or when the
    /// plugin registry is unavailable.
    /// </summary>
    private List<AITool> ApplyPluginBoundaryIfPluginSkill(SkillDefinition skill, List<AITool> tools)
    {
        if (string.IsNullOrEmpty(skill.PluginSource))
            return tools;

        var pluginRegistry = _serviceProvider.GetService<IPluginRegistry>();
        var loadedPlugin = pluginRegistry?.GetPlugin(skill.PluginSource);
        return loadedPlugin is null
            ? tools
            : ApplyPluginToolBoundary(tools, loadedPlugin.Declaration);
    }

    /// <summary>
    /// The single exit every resolution path returns through: drops tools whose names collide with a
    /// reserved <see cref="PlanCapabilities"/> name, then governance-wraps what survives. Every public
    /// build method funnels here, so a tool that reaches an agent's callable surface has passed both
    /// checks exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the reserved filter lives here and not at boot.</strong>
    /// <c>ReservedPlanCapabilityGuard</c> catches first-party keyed <c>ITool</c> registrations by
    /// scanning DI descriptors at composition, but MCP-client and plugin-manifest tools are discovered
    /// at <em>runtime</em> from third-party sources — no boot-time scan can see them. Their names land
    /// in the same flat, case-insensitively matched string space that <c>CapabilityEnvelope.AllowedTools</c>
    /// authorizes plan capabilities out of, so an external server publishing a tool called
    /// <c>rag_retrieval</c> would be handed to the model by any plan envelope that grants retrieval —
    /// and an envelope granting such a tool would grant plan inference. Excluding the name here, before
    /// it is ever wrapped or published, closes that in both directions.
    /// </para>
    /// <para>
    /// <strong>Drop, never throw.</strong> A third party editing its tool list must not be able to take
    /// down every agent turn in the host, so the collision degrades to one loud <c>Error</c> log plus a
    /// governance-violation counter and the run continues without that tool. The boot guard stays the
    /// louder, fail-fast check for code we control; this is the safe-degradation check for code we do not.
    /// </para>
    /// </remarks>
    /// <param name="tools">The deduplicated tools resolved by one build path.</param>
    /// <param name="source">Human-readable description of where the tools were resolved from, for the drop log.</param>
    private List<AITool> FinalizeChain(IEnumerable<AITool> tools, string source)
    {
        var permitted = new List<AITool>();
        foreach (var tool in tools)
        {
            if (PlanCapabilities.IsReserved(tool.Name))
                ReportReservedPlanCapabilityCollision(tool.Name, source);
            else
                permitted.Add(tool);
        }

        return WrapGoverned(permitted);
    }

    /// <summary>
    /// Records a runtime-sourced tool that was dropped for colliding with a reserved plan-capability
    /// name — loudly, because it means a third-party source is publishing a name the plan engine owns
    /// and that source needs re-keying.
    /// </summary>
    private void ReportReservedPlanCapabilityCollision(string toolName, string source)
    {
        var reserved = PlanCapabilities.ReservedNames
            .First(name => string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase));

        _logger.LogError(
            "Reserved plan-capability collision: tool '{ToolName}' from {ToolSource} matches reserved " +
            "plan capability '{ReservedName}' and was excluded from the callable tool chain. Plan " +
            "capabilities are authorized out of the same CapabilityEnvelope.AllowedTools string space as " +
            "tool names, so granting that capability would otherwise also grant this tool. Re-key the tool " +
            "at its source.",
            toolName, source, reserved);

        // Tagged with the normalised reserved name rather than the verbatim tool name: a case variant is
        // attacker-influenced text and would put unbounded cardinality on the metric.
        GovernanceMetrics.Violations.Add(1,
            new KeyValuePair<string, object?>(GovernanceConventions.PolicyName, ReservedPlanCapabilityPolicy),
            new KeyValuePair<string, object?>(GovernanceConventions.ToolName, reserved));
    }

    /// <summary>
    /// Describes the resolution path a tool arrived on, naming the owning plugin when the skill is
    /// plugin-sourced so a dropped collision points at the source that needs re-keying.
    /// </summary>
    private static string DescribeSource(SkillDefinition skill, string mode) =>
        string.IsNullOrEmpty(skill.PluginSource)
            ? $"{mode} for skill '{skill.Id}'"
            : $"{mode} for skill '{skill.Id}' (plugin '{skill.PluginSource}')";

    /// <summary>
    /// Wraps each callable tool function in a <see cref="GovernedAIFunction"/> so a per-invocation
    /// governance check runs before the tool executes. Non-function tools and already-wrapped
    /// functions pass through unchanged. The wrapper is inert unless tool-invocation enforcement is
    /// enabled and a governor is ambient for the turn, so this adds no behaviour when governance is off.
    /// Applied at this single shared builder so every agent-callable tool — keyed-DI, MCP, or
    /// skill-provided — is governed exactly once.
    /// </summary>
    private static List<AITool> WrapGoverned(IEnumerable<AITool> tools)
        => tools
            .Select(t => t is AIFunction fn and not GovernedAIFunction ? new GovernedAIFunction(fn) : t)
            .ToList();

    /// <inheritdoc />
    public List<AITool> BuildToolsByName(IReadOnlyList<string> toolNames)
    {
        var tools = new List<AITool>();
        foreach (var name in toolNames)
        {
            var resolved = ResolveToolByName(name);
            if (resolved != null)
                tools.AddRange(resolved);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return FinalizeChain(tools.Where(t => seen.Add(t.Name)), "keyed-DI resolution by name");
    }

    /// <inheritdoc />
    public async Task<List<AITool>> BuildMergedToolsAsync(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options,
        IReadOnlyList<string>? allowedTools = null,
        CancellationToken cancellationToken = default)
    {
        var merged = await BuildMergedToolsWithSourcesAsync(skills, options, allowedTools, cancellationToken);
        return merged.Tools.ToList();
    }

    /// <inheritdoc />
    public async Task<MergedToolChain> BuildMergedToolsWithSourcesAsync(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options,
        IReadOnlyList<string>? allowedTools = null,
        CancellationToken cancellationToken = default)
    {
        // MCP-sourced tool names accumulate as resolution happens — no extra round trip.
        // Injected-mode skills contribute every MCP tool; managed-mode skills contribute
        // only tools whose ToolDeclaration was satisfied by MCP first.
        var mcpCollector = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allTools = new List<AITool>();
        foreach (var skill in skills)
        {
            var skillTools = await BuildToolsAsync(skill, options, mcpCollector, cancellationToken);
            allTools.AddRange(skillTools);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = allTools.Where(t => seen.Add(t.Name)).ToList();

        // A null allowlist means no restriction; a non-null one is an active restriction that keeps
        // only the listed tools — an empty (but non-null) list therefore denies every tool, which is
        // how an agent tool ceiling disjoint from the skills' tools collapses to no tools rather than all.
        if (allowedTools is not null)
        {
            var allowed = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
            deduplicated = deduplicated.Where(t => allowed.Contains(t.Name)).ToList();
        }

        // Filter MCP names down to what actually survived dedup + AllowedTools so the
        // panel doesn't claim a tool was MCP-sourced when it was governance-filtered out.
        var survivingNames = new HashSet<string>(deduplicated.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var attributedMcp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in mcpCollector)
            if (survivingNames.Contains(name))
                attributedMcp.Add(name);

        return new MergedToolChain(deduplicated, attributedMcp);
    }

    internal static List<AITool> ApplyPluginToolBoundary(List<AITool> tools, PluginDeclaration declaration)
    {
        if (declaration.AllowedTools is { Count: > 0 } allowed)
        {
            var allowSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => allowSet.Contains(t.Name)).ToList();
        }

        if (declaration.DeniedTools is { Count: > 0 } denied)
        {
            var denySet = new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => !denySet.Contains(t.Name)).ToList();
        }

        return tools;
    }

    private async Task<IEnumerable<AITool>?> ProvisionToolAsync(
        Domain.AI.Tools.ToolDeclaration declaration,
        ISet<string> mcpCollector,
        CancellationToken cancellationToken = default)
    {
        // Reference-only MCP: a bundle run resolves a tool from an MCP server only when the caller's
        // envelope grants that server; otherwise the MCP attempt is skipped and resolution falls through
        // to keyed DI (itself governed at invocation time). Off the bundle path every server is permitted.
        if (_mcpToolProvider != null && IsMcpServerAllowed(declaration.Name))
        {
            try
            {
                var mcpTools = await _mcpToolProvider.GetToolsAsync(declaration.Name, cancellationToken);
                if (mcpTools?.Count > 0)
                {
                    _logger.LogDebug("Resolved tool {ToolName} from MCP server", declaration.Name);
                    foreach (var t in mcpTools) mcpCollector.Add(t.Name);
                    return mcpTools;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP resolution failed for {ToolName}, trying keyed DI", declaration.Name);
            }
        }

        var resolved = ResolveToolByName(declaration.Name);
        if (resolved != null)
            return resolved;

        if (declaration.HasFallback && !declaration.FallbackIsManual)
        {
            resolved = ResolveToolByName(declaration.Fallback!);
            if (resolved != null)
            {
                _logger.LogInformation("Using fallback tool {Fallback} for {ToolName}",
                    declaration.Fallback, declaration.Name);
                return resolved;
            }
        }

        if (!declaration.Optional && !declaration.FallbackIsManual)
        {
            throw new InvalidOperationException(
                $"Required tool '{declaration.Name}' could not be resolved. " +
                "Ensure the tool is registered via keyed DI or available from an MCP server. " +
                "Mark the tool declaration as Optional = true if the skill can function without it.");
        }

        return null;
    }

    /// <summary>
    /// Resolves the MCP tools to inject for an Injected-mode skill. Off the bundle path this is every
    /// configured server's tools (the historical behaviour). On a bundle run it contacts <em>only</em> the
    /// servers the caller's envelope grants — never enumerating every host server — so an ungranted server
    /// is never reached at all (no side-effect connection, no tool-schema disclosure), closing
    /// SSRF-by-construction. An empty grant yields no MCP tools.
    /// </summary>
    private async Task<IReadOnlyList<IList<AITool>>> ResolveInjectedMcpToolsAsync(CancellationToken cancellationToken)
    {
        var envelope = CapabilityEnvelopeAccessor.Current;
        if (envelope is null)
            return [.. (await _mcpToolProvider!.GetAllToolsAsync(cancellationToken)).Values];

        // Reference-only MCP on a bundle run: contact ONLY the granted servers, concurrently. A server that
        // fails is skipped (its tools are simply unavailable this turn) rather than failing the whole build.
        var fetches = envelope.AllowedMcpServers.Select(async server =>
        {
            try
            {
                return await _mcpToolProvider!.GetToolsAsync(server, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Capability envelope: granted MCP server '{Server}' could not be reached — skipped", server);
                return null;
            }
        });

        var granted = (await Task.WhenAll(fetches))
            .Where(t => t is { Count: > 0 })
            .Cast<IList<AITool>>()
            .ToList();

        _logger.LogInformation(
            "Capability envelope: injecting MCP tools from {Count} granted server(s); ungranted servers are not contacted",
            granted.Count);

        return granted;
    }

    /// <summary>
    /// Whether the ambient capability envelope permits reaching the named MCP server on the managed
    /// resolution path. Off the bundle path no envelope is published, so this returns
    /// <see langword="true"/> and every server passes through unchanged. On a bundle run only servers named
    /// in the caller's envelope are permitted; a denied server is logged and never contacted, so a bundle
    /// can never reach a host MCP server it was not granted.
    /// </summary>
    private bool IsMcpServerAllowed(string serverName)
    {
        var envelope = CapabilityEnvelopeAccessor.Current;
        if (envelope is null || envelope.GrantsMcpServer(serverName))
            return true;

        _logger.LogInformation(
            "Capability envelope: MCP server '{Server}' is outside the bundle run's grant — not contacted and its tools excluded",
            serverName);
        return false;
    }

    private IEnumerable<AITool>? ResolveToolByName(string toolName)
    {
        var tool = _serviceProvider.GetKeyedService<ITool>(toolName);
        if (tool == null)
            return null;

        if (_toolConverter != null)
        {
            var converted = _toolConverter.Convert(tool);
            if (converted != null)
                return [converted];
        }

        _logger.LogWarning("Tool {ToolName} found in keyed DI but no IToolConverter available to convert it", toolName);
        return [];
    }
}
