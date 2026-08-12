using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Skills;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Resolves and assembles tools for agent execution contexts. Supports three resolution
/// modes — Injected (all MCP tools passed through), Managed with ToolDeclarations (MCP-first
/// with keyed DI fallback), and Managed with AllowedTools (simple name-based resolution).
/// Applies plugin governance boundary filtering (AllowedTools/DeniedTools) for plugin-sourced skills.
/// </summary>
public partial class ToolChainBuilder : IToolChainBuilder
{
    private readonly ILogger<ToolChainBuilder> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IToolConverter? _toolConverter;
    private readonly IMcpToolProvider? _mcpToolProvider;
    private readonly IMcpToolSurfaceScanner? _surfaceScanner;
    private readonly IOptionsMonitor<AIConfig>? _aiConfig;

    public ToolChainBuilder(
        ILogger<ToolChainBuilder> logger,
        IServiceProvider serviceProvider,
        IToolConverter? toolConverter = null,
        IMcpToolProvider? mcpToolProvider = null,
        IMcpToolSurfaceScanner? surfaceScanner = null,
        IOptionsMonitor<AIConfig>? aiConfig = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _toolConverter = toolConverter;
        _mcpToolProvider = mcpToolProvider;
        _surfaceScanner = surfaceScanner;
        _aiConfig = aiConfig;
    }

    /// <summary>
    /// A tool paired with where it was resolved from — recorded at the moment of resolution, before
    /// dedup, the reserved-name filter, or governance wrapping run. <see langword="null"/>
    /// <see cref="McpServerName"/> means first-party (keyed DI or caller-supplied); a non-null value
    /// names the MCP server that advertised it.
    /// </summary>
    /// <remarks>
    /// This is the single source of truth for "where did this tool come from" used by
    /// <c>ResolveSurvivingTools</c> (see <c>ToolChainBuilder.Surface.cs</c>) to decide first-party
    /// precedence. Earlier revisions tried to reconstruct provenance after the fact — by tool-instance
    /// identity, which does not survive <see cref="WrapGoverned"/>'s per-skill wrapping, and by
    /// (name, description) content signature, which an attacker can defeat by copying a first-party
    /// tool's description verbatim onto a same-named MCP tool, making both instances indistinguishable
    /// and causing the exclusion to drop them both. Tracking provenance positionally as each tool is
    /// produced makes both failure modes structurally impossible: origin is a label attached at
    /// creation, never re-derived from content.
    /// </remarks>
    private readonly record struct ProvisionedTool(AITool Tool, string? McpServerName);

    /// <summary>
    /// Resolves one skill's tools. Runs the same first-party-precedence and collision/shadowing/drift
    /// policy <see cref="BuildMergedToolsWithSourcesAsync"/> applies across skills — a single skill can
    /// still contribute a tool from more than one MCP server (multiple <c>ToolDeclarations</c>), so this
    /// path needs the same protection, not a narrower one just because it has only one skill's tools to
    /// look at.
    /// </summary>
    /// <inheritdoc />
    public async Task<List<AITool>> BuildToolsAsync(SkillDefinition skill, SkillAgentOptions options, CancellationToken cancellationToken = default)
    {
        var provisioned = await BuildProvisionedToolsAsync(skill, options, cancellationToken);
        var (tools, _) = ResolveSurvivingTools(provisioned);
        return tools;
    }

    private Task<List<ProvisionedTool>> BuildProvisionedToolsAsync(
        SkillDefinition skill,
        SkillAgentOptions options,
        CancellationToken cancellationToken)
        => skill.Mode == SkillMode.Injected && _mcpToolProvider != null
            ? BuildInjectedModeToolsAsync(skill, options, cancellationToken)
            : BuildManagedModeToolsAsync(skill, options, cancellationToken);

    private async Task<List<ProvisionedTool>> BuildInjectedModeToolsAsync(
        SkillDefinition skill,
        SkillAgentOptions options,
        CancellationToken cancellationToken)
    {
        var injected = new List<ProvisionedTool>();
        foreach (var (serverName, serverTools) in await ResolveInjectedMcpToolsAsync(cancellationToken))
        {
            // Mirrors ProvisionToolAsync's Managed-mode wrapping: a bundle-owned server's tools must be
            // published under a namespaced name (never the bare, bundle-chosen one a malicious bundle
            // controls), because that published name is exactly what gets checked against
            // CapabilityEnvelope.AllowedTools at invocation time. BundleRunExecutor grants the namespaced
            // name via this same BundleOwnedMcpToolNaming.BuildToolName; publishing the bare name here
            // would both deny every legitimate call (never in AllowedTools) and reopen the name-collision
            // privilege escalation ProvisionToolAsync already closes on the Managed path.
            var isBundleOwned = BundleOwnedMcpToolNaming.IsNamespacedServerName(serverName);
            foreach (var t in serverTools)
            {
                var published = isBundleOwned && t is AIFunction fn
                    ? (AITool)new NamespacedAIFunction(fn, BundleOwnedMcpToolNaming.BuildToolName(serverName, t.Name))
                    : t;
                injected.Add(new ProvisionedTool(published, serverName));
            }
        }

        if (options.AdditionalTools?.Count > 0)
            foreach (var t in options.AdditionalTools)
                injected.Add(new ProvisionedTool(t, null));

        injected = ApplyPluginBoundaryIfPluginSkill(skill, injected);

        _logger.LogInformation(
            "Injected mode: skill {SkillId} from plugin {Plugin} received {Count} MCP tools",
            skill.Id, skill.PluginSource, injected.Count);

        // Deliberately not deduped by name here: two servers advertising the same name is exactly the
        // ambiguity ResolveSurvivingTools' collision policy exists to catch (withhold both), and a
        // name-only dedup this early would silently pick a first-occurrence winner before that policy
        // — or a first-party name check — ever sees more than one candidate.
        return FinalizeChain(injected, DescribeSource(skill, "injected MCP tool resolution"));
    }

    private async Task<List<ProvisionedTool>> BuildManagedModeToolsAsync(
        SkillDefinition skill,
        SkillAgentOptions options,
        CancellationToken cancellationToken)
    {
        var managed = new List<ProvisionedTool>();

        if (skill.Tools?.Count > 0)
            foreach (var t in skill.Tools)
                managed.Add(new ProvisionedTool(t, null));

        if (skill.ToolDeclarations?.Count > 0)
        {
            var provisionTasks = skill.ToolDeclarations.Select(d => ProvisionToolAsync(d, cancellationToken));
            var results = await Task.WhenAll(provisionTasks);
            foreach (var provisioned in results)
                if (provisioned != null)
                    managed.AddRange(provisioned);
        }

        if (skill.AllowedTools?.Count > 0)
        {
            foreach (var toolName in skill.AllowedTools)
            {
                var resolved = ResolveToolByName(toolName);
                if (resolved != null)
                    foreach (var t in resolved)
                        managed.Add(new ProvisionedTool(t, null));
            }
        }

        if (options.AdditionalTools?.Count > 0)
            foreach (var t in options.AdditionalTools)
                managed.Add(new ProvisionedTool(t, null));

        managed = ApplyPluginBoundaryIfPluginSkill(skill, managed);

        // Deliberately not deduped by name here — see the matching note in BuildInjectedModeToolsAsync.
        // Two ToolDeclarations in this same skill resolving from two different MCP servers to the same
        // name is exactly the collision ResolveSurvivingTools must see both candidates for.
        return FinalizeChain(managed, DescribeSource(skill, "managed tool resolution"));
    }

    /// <summary>
    /// Applies the owning plugin's AllowedTools/DeniedTools boundary to <paramref name="provisioned"/>
    /// whenever the skill is plugin-sourced and the plugin is loaded. This runs on both the
    /// Injected and Managed resolution paths so a plugin's <c>DeniedTools</c> are enforced
    /// regardless of how the skill resolves its tools. A no-op for built-in skills or when the
    /// plugin registry is unavailable.
    /// </summary>
    private List<ProvisionedTool> ApplyPluginBoundaryIfPluginSkill(SkillDefinition skill, List<ProvisionedTool> provisioned)
    {
        if (string.IsNullOrEmpty(skill.PluginSource))
            return provisioned;

        var pluginRegistry = _serviceProvider.GetService<IPluginRegistry>();
        var loadedPlugin = pluginRegistry?.GetPlugin(skill.PluginSource);
        if (loadedPlugin is null)
            return provisioned;

        return ApplyPluginToolBoundary(provisioned, loadedPlugin.Declaration);
    }

    /// <summary>
    /// Filters <paramref name="provisioned"/> down to the entries whose <see cref="AITool"/> instance
    /// appears (by reference) in <paramref name="survivors"/>, preserving each entry's provenance tag.
    /// Safe to use only where <paramref name="survivors"/> was produced by a pure filter over the same
    /// instances (never a wrap/transform) — both <see cref="ApplyPluginToolBoundary"/> and
    /// <see cref="ReservedPlanCapabilityFilter.Exclude"/> qualify: each only removes elements, so a
    /// surviving instance is always reference-equal to the one that went in.
    /// </summary>
    private static List<ProvisionedTool> KeepSurviving(List<ProvisionedTool> provisioned, IEnumerable<AITool> survivors)
    {
        var survivorSet = new HashSet<AITool>(survivors, ReferenceEqualityComparer.Instance);
        return provisioned.Where(p => survivorSet.Contains(p.Tool)).ToList();
    }

    /// <summary>
    /// The single exit every resolution path in <em>this builder</em> returns through: drops tools whose
    /// names collide with a reserved <see cref="Domain.AI.Planner.PlanCapabilities"/> name (via the shared
    /// <see cref="ReservedPlanCapabilityFilter"/>), then governance-wraps what survives. Every public
    /// build method funnels here, so a tool the builder publishes has passed both checks exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reserved-name check runs here rather than at boot because <c>ReservedPlanCapabilityGuard</c>
    /// can only see first-party keyed <c>ITool</c> registrations at composition time, while MCP-client and
    /// plugin-manifest tools are discovered at <em>runtime</em>. See
    /// <see cref="ReservedPlanCapabilityFilter"/> for why the collision matters and why it degrades rather
    /// than throwing.
    /// </para>
    /// <para>
    /// <strong>This is not the only channel.</strong> The framework also merges tools contributed through
    /// <c>AIContext.Tools</c> by <c>AIContextProvider</c>s, which never pass through this builder;
    /// <c>GoverningToolContextProvider</c> applies the same shared filter on that channel. Both call the
    /// one helper so the two enforcement points cannot drift.
    /// </para>
    /// </remarks>
    /// <param name="provisioned">The deduplicated, attributed tools resolved by one build path.</param>
    /// <param name="source">Human-readable description of where the tools were resolved from, for the drop log.</param>
    private List<ProvisionedTool> FinalizeChain(List<ProvisionedTool> provisioned, string source)
    {
        var survivors = ReservedPlanCapabilityFilter.Exclude(provisioned.Select(p => p.Tool), source, _logger);
        var afterReservedFilter = KeepSurviving(provisioned, survivors);

        var wrapped = WrapGoverned(afterReservedFilter.Select(p => p.Tool));
        return afterReservedFilter.Zip(wrapped, (p, w) => p with { Tool = w }).ToList();
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
        var provisioned = tools.Where(t => seen.Add(t.Name)).Select(t => new ProvisionedTool(t, null)).ToList();
        return FinalizeChain(provisioned, "keyed-DI resolution by name").ConvertAll(p => p.Tool);
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
        var allProvisioned = new List<ProvisionedTool>();
        foreach (var skill in skills)
        {
            var skillTools = await BuildProvisionedToolsAsync(skill, options, cancellationToken);
            allProvisioned.AddRange(skillTools);
        }

        var (deduplicated, attributedMcp) = ResolveSurvivingTools(allProvisioned);

        // A null allowlist means no restriction; a non-null one is an active restriction that keeps
        // only the listed tools — an empty (but non-null) list therefore denies every tool, which is
        // how an agent tool ceiling disjoint from the skills' tools collapses to no tools rather than all.
        if (allowedTools is not null)
        {
            var allowed = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
            deduplicated = deduplicated.Where(t => allowed.Contains(t.Name)).ToList();

            // The panel must not claim a tool was MCP-sourced when AllowedTools just filtered it out.
            attributedMcp.IntersectWith(deduplicated.Select(t => t.Name));
        }

        return new MergedToolChain(deduplicated, attributedMcp);
    }

    /// <summary>
    /// Applies a plugin's AllowedTools/DeniedTools boundary. Operates on <see cref="ProvisionedTool"/>
    /// directly rather than <see cref="AITool"/> — this filter has exactly one caller
    /// (<see cref="ApplyPluginBoundaryIfPluginSkill"/>) and is a pure name-based <c>Where</c>, so there
    /// is no shared, provenance-unaware consumer to preserve compatibility with (contrast
    /// <see cref="ReservedPlanCapabilityFilter.Exclude"/>, which is shared with
    /// <c>GoverningToolContextProvider</c> and must stay on <see cref="AITool"/>). Taking the
    /// provenance-carrying type directly means a survivor's origin tag never needs to be recovered
    /// after the fact.
    /// </summary>
    private static List<ProvisionedTool> ApplyPluginToolBoundary(List<ProvisionedTool> tools, PluginDeclaration declaration)
    {
        if (declaration.AllowedTools is { Count: > 0 } allowed)
        {
            var allowSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => allowSet.Contains(t.Tool.Name)).ToList();
        }

        if (declaration.DeniedTools is { Count: > 0 } denied)
        {
            var denySet = new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => !denySet.Contains(t.Tool.Name)).ToList();
        }

        return tools;
    }

    /// <summary>
    /// Resolves one <see cref="Domain.AI.Tools.ToolDeclaration"/>, trying MCP first and falling back to
    /// keyed DI. Deliberately touches no shared state: every tool it returns already carries its own
    /// provenance tag, so concurrent calls from <see cref="BuildProvisionedToolsAsync"/>'s
    /// <c>Task.WhenAll</c> never race on a mutable collection — each task's result is folded into the
    /// caller's list sequentially, after every task has completed.
    /// </summary>
    private async Task<List<ProvisionedTool>?> ProvisionToolAsync(
        Domain.AI.Tools.ToolDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        // Reference-only MCP: a bundle run resolves a tool from an MCP server only when the caller's
        // envelope grants that server; otherwise the MCP attempt is skipped and resolution falls through
        // to keyed DI (itself governed at invocation time). Off the bundle path every server is permitted.
        var (effectiveServerName, isBundleOwned) = ResolveEffectiveMcpServerName(declaration.Name);
        if (_mcpToolProvider != null && effectiveServerName is not null)
        {
            try
            {
                // In managed mode, a ToolDeclaration's Name is the MCP server name (or, on a bundle run,
                // the bundle-agnostic name a namespaced grant resolved to) — GetToolsAsync returns that
                // server's whole tool list, which is why every tool it returns is attributed to
                // effectiveServerName below.
                var mcpTools = await _mcpToolProvider.GetToolsAsync(effectiveServerName, cancellationToken);
                if (mcpTools?.Count > 0)
                {
                    _logger.LogDebug(
                        "Resolved tool {ToolName} from MCP server {ServerName}", declaration.Name, effectiveServerName);

                    // A bundle-owned server was reached only via the namespaced suffix fallback — its
                    // tools are published and governed under a namespaced name (never the bare,
                    // bundle-chosen one) so a malicious bundle cannot get a real host tool auto-granted
                    // by advertising a same-named tool of its own. See BundleOwnedMcpToolNaming.
                    var published = isBundleOwned
                        ? mcpTools.Select(t => t is AIFunction fn
                            ? (AITool)new NamespacedAIFunction(
                                fn, BundleOwnedMcpToolNaming.BuildToolName(effectiveServerName, t.Name))
                            : t)
                        : mcpTools;

                    return published.Select(t => new ProvisionedTool(t, effectiveServerName)).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP resolution failed for {ToolName}, trying keyed DI", declaration.Name);
            }
        }

        // Everything from here on is keyed-DI, not MCP — first-party.
        var resolved = ResolveToolByName(declaration.Name);
        if (resolved != null)
            return resolved.Select(t => new ProvisionedTool(t, null)).ToList();

        if (declaration.HasFallback && !declaration.FallbackIsManual)
        {
            resolved = ResolveToolByName(declaration.Fallback!);
            if (resolved != null)
            {
                _logger.LogInformation("Using fallback tool {Fallback} for {ToolName}",
                    declaration.Fallback, declaration.Name);
                return resolved.Select(t => new ProvisionedTool(t, null)).ToList();
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
    private async Task<IReadOnlyList<(string ServerName, IList<AITool> Tools)>> ResolveInjectedMcpToolsAsync(CancellationToken cancellationToken)
    {
        var envelope = CapabilityEnvelopeAccessor.Current;
        if (envelope is null)
        {
            var allByServer = await _mcpToolProvider!.GetAllToolsAsync(cancellationToken);
            return [.. allByServer.Select(kvp => (kvp.Key, kvp.Value))];
        }

        // Reference-only MCP on a bundle run: contact ONLY the granted servers, concurrently. A server that
        // fails is skipped (its tools are simply unavailable this turn) rather than failing the whole build.
        var fetches = envelope.AllowedMcpServers.Select(server => FetchServerToolsAsync(server, cancellationToken));

        var granted = (await Task.WhenAll(fetches))
            .Where(t => t.Tools is { Count: > 0 })
            .Select(t => (t.Server, Tools: t.Tools!))
            .ToList();

        _logger.LogInformation(
            "Capability envelope: injecting MCP tools from {Count} granted server(s); ungranted servers are not contacted",
            granted.Count);

        return granted;
    }

    /// <summary>
    /// Fetches one granted server's tools, returning a null <c>Tools</c> when the server could not be
    /// reached rather than throwing — a failed server is skipped, not a failed build. Declared with an
    /// explicit nullable tuple return type rather than as an inline lambda: an anonymous async lambda
    /// with two return statements of differing nullability infers the tuple element as non-nullable
    /// from the first return, which silently mistypes the failure branch's <see langword="null"/>.
    /// </summary>
    private async Task<(string Server, IList<AITool>? Tools)> FetchServerToolsAsync(string server, CancellationToken cancellationToken)
        => (server, await McpToolFetch.TryGetToolsAsync(_mcpToolProvider!, server, "Capability envelope", _logger, cancellationToken));

    /// <summary>
    /// Resolves the MCP server name to actually contact for a skill's declared, bundle-agnostic server
    /// name (e.g. <c>"epr-mcp"</c>) on the managed resolution path, and whether that resolution went
    /// through the namespaced (bundle-owned) fallback rather than an exact grant — the caller uses that
    /// flag to decide whether the resolved tools must be published under a namespaced name (see
    /// <see cref="BundleOwnedMcpToolNaming"/>).
    /// </summary>
    /// <remarks>
    /// Off the bundle path no envelope is published, so the declared name passes through unchanged
    /// (<c>IsBundleOwnedFallback: false</c>) and every server is permitted. On a bundle run, an exact
    /// grant for the declared name wins outright — a host-configured, non-namespaced server is unaffected
    /// by the fallback below. A skill author cannot know their bundle's future id at authoring time, so a
    /// bundle's own server is granted under a namespaced key (<c>{bundleId}:{declaredName}</c>) that never
    /// exact-matches the declaration; when the armed envelope contains exactly one grant ending in
    /// <c>:{declaredName}</c>, that full namespaced name is resolved instead, flagged
    /// <c>IsBundleOwnedFallback: true</c>. Two or more matching suffixes is ambiguous and denied rather
    /// than guessed. Returns a <see langword="null"/> server name when neither resolves, logged and never
    /// contacted — a bundle can never reach a host MCP server it was not granted.
    /// </remarks>
    private (string? ServerName, bool IsBundleOwnedFallback) ResolveEffectiveMcpServerName(string declaredName)
    {
        var envelope = CapabilityEnvelopeAccessor.Current;
        if (envelope is null || envelope.GrantsMcpServer(declaredName))
            return (declaredName, false);

        var suffix = ":" + declaredName;
        var matches = envelope.AllowedMcpServers
            .Where(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
            return (matches[0], true);

        _logger.LogInformation(
            "Capability envelope: MCP server '{Server}' is outside the bundle run's grant — not contacted and its tools excluded",
            declaredName);
        return (null, false);
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
