using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Skills;
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
            return WrapGoverned(tools.Where(t => seen.Add(t.Name)));
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
        return WrapGoverned(tools.Where(t => seen2.Add(t.Name)));
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
