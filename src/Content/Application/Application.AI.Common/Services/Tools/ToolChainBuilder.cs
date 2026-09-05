using System.Collections.Concurrent;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
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
    private readonly IToolCompositionAnalyzer? _compositionAnalyzer;
    private readonly ToolCompositionReporter? _compositionReporter;
    private readonly IToolCallOncePolicy? _callOncePolicy;

    public ToolChainBuilder(
        ILogger<ToolChainBuilder> logger,
        IServiceProvider serviceProvider,
        IToolConverter? toolConverter = null,
        IMcpToolProvider? mcpToolProvider = null,
        IMcpToolSurfaceScanner? surfaceScanner = null,
        IOptionsMonitor<AIConfig>? aiConfig = null,
        IToolCompositionAnalyzer? compositionAnalyzer = null,
        ToolCompositionReporter? compositionReporter = null,
        IToolCallOncePolicy? callOncePolicy = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _toolConverter = toolConverter;
        _mcpToolProvider = mcpToolProvider;
        _surfaceScanner = surfaceScanner;
        _aiConfig = aiConfig;
        _compositionAnalyzer = compositionAnalyzer;
        _compositionReporter = compositionReporter;
        _callOncePolicy = callOncePolicy;
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
        var callOnceCandidates = new ConcurrentDictionary<AITool, byte>(ReferenceEqualityComparer.Instance);
        var provisioned = await BuildProvisionedToolsAsync(skill, options, callOnceCandidates, cancellationToken);
        var (tools, _) = ResolveSurvivingTools(provisioned);

        // Registration happens here, against the truly final published surface — the point at which
        // every filter (plugin boundary, reserved-capability, first-party/cross-server precedence,
        // drift withholding) has already run. See RegisterSurvivingCallOnceTools's remarks for why
        // registering any earlier is unsafe.
        RegisterSurvivingCallOnceTools(tools, callOnceCandidates, skill.Id);

        // The third whole-agent-set exit — see ApplyCompositionTaint's remarks. Unlike the per-skill
        // BuildInjectedModeToolsAsync/BuildManagedModeToolsAsync it is built from, this method's return
        // value IS the complete agent tool set whenever a caller resolves an agent from a single skill,
        // so it gets the same treatment as the two multi-skill exits rather than being left uncovered.
        var agentName = options.AgentNameOverride ?? skill.Name;
        return ApplyCompositionTaint(tools, agentName);
    }

    private Task<List<ProvisionedTool>> BuildProvisionedToolsAsync(
        SkillDefinition skill,
        SkillAgentOptions options,
        ConcurrentDictionary<AITool, byte> callOnceCandidates,
        CancellationToken cancellationToken)
        => skill.Mode == SkillMode.Injected && _mcpToolProvider != null
            ? BuildInjectedModeToolsAsync(skill, options, cancellationToken)
            : BuildManagedModeToolsAsync(skill, options, callOnceCandidates, cancellationToken);

    private async Task<List<ProvisionedTool>> BuildInjectedModeToolsAsync(
        SkillDefinition skill,
        SkillAgentOptions options,
        CancellationToken cancellationToken)
    {
        var envelope = CapabilityEnvelopeAccessor.Current;
        var injected = new List<ProvisionedTool>();
        foreach (var (serverName, serverTools) in await ResolveInjectedMcpToolsAsync(envelope, cancellationToken))
        {
            // The authoritative source for "is this granted server bundle-owned": CapabilityEnvelope.BundleOwnedMcpServers,
            // stamped once by RunBundleCommandHandler from the staged bundle's own registration. A server
            // name's SHAPE cannot answer this — PluginLoader namespaces a host-installed plugin's own MCP
            // servers under the identical "{Prefix}:{ServerName}" convention, into the same shared server
            // config a bundle registers into, so a colon in the name is not evidence of bundle ownership.
            var isBundleOwned = envelope?.IsBundleOwnedMcpServer(serverName) ?? false;
            foreach (var t in serverTools)
                injected.Add(new ProvisionedTool(PublishServerTool(t, serverName, isBundleOwned), serverName));
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
        //
        // No call-once candidates flow through this path: Injected mode never calls ProvisionToolAsync
        // (the only place a ToolDeclaration's CallOncePerConversation is tagged), so there is nothing
        // for FinalizeChain to carry forward here.
        return FinalizeChain(injected, DescribeSource(skill, "injected MCP tool resolution"));
    }

    private async Task<List<ProvisionedTool>> BuildManagedModeToolsAsync(
        SkillDefinition skill,
        SkillAgentOptions options,
        ConcurrentDictionary<AITool, byte> callOnceCandidates,
        CancellationToken cancellationToken)
    {
        var managed = new List<ProvisionedTool>();

        if (skill.Tools?.Count > 0)
            foreach (var t in skill.Tools)
                managed.Add(new ProvisionedTool(t, null));

        if (skill.ToolDeclarations?.Count > 0)
        {
            var provisionTasks = skill.ToolDeclarations.Select(d => ProvisionToolAsync(d, callOnceCandidates, cancellationToken));
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
        //
        // callOnceCandidates is NOT registered against IToolCallOncePolicy here. It is only carried
        // forward across FinalizeChain's GovernedAIFunction wrap (see WrapGoverned) so a true
        // whole-agent-set exit (BuildToolsAsync, BuildMergedToolsWithSourcesAsync) can register it
        // later, against the FULLY resolved surface — see RegisterSurvivingCallOnceTools's remarks for
        // why registering at this per-skill, pre-cross-skill-dedup point was unsafe.
        return FinalizeChain(managed, DescribeSource(skill, "managed tool resolution"), callOnceCandidates);
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

        // #524: a boundary entry that matches no real tool is provably broken, not just
        // permissive — most dangerously for DeniedTools, documented as bypass-immune. Once
        // PluginToolBoundaryTracker has proven that (see its remarks), the boundary can no longer
        // be trusted, so this denies every tool from the plugin rather than run with a
        // partially-broken policy.
        if (pluginRegistry!.IsBoundaryFaulted(skill.PluginSource))
        {
            _logger.LogWarning(
                "Plugin '{Plugin}' tool boundary is faulted (an AllowedTools/DeniedTools entry " +
                "matches no known tool) — denying all tools for skill '{Skill}'",
                skill.PluginSource, skill.Id);
            return [];
        }

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
    /// Publishes one server-resolved tool under its governed name. A bundle-owned server's
    /// <see cref="AIFunction"/> tools are wrapped in <see cref="NamespacedAIFunction"/> under
    /// <see cref="BundleOwnedMcpToolNaming.BuildToolName"/> — never the bare, bundle-chosen name a
    /// malicious bundle controls — because that published name is exactly what gets checked against
    /// <c>CapabilityEnvelope.AllowedTools</c> at invocation time; <c>BundleRunExecutor</c> grants the
    /// SAME namespaced name via this same function, so the two can never drift apart. Everything else
    /// (a non-bundle-owned server, or a non-<see cref="AIFunction"/> tool) passes through unchanged.
    /// Shared by both MCP resolution paths — <see cref="BuildInjectedModeToolsAsync"/> and
    /// <see cref="ProvisionToolAsync"/> — so this decision is made in exactly one place, never
    /// independently re-decided (and potentially missed) per call site.
    /// </summary>
    private static AITool PublishServerTool(AITool tool, string serverName, bool isBundleOwned) =>
        isBundleOwned && tool is AIFunction fn
            ? new NamespacedAIFunction(fn, BundleOwnedMcpToolNaming.BuildToolName(serverName, tool.Name))
            : tool;

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
    /// <param name="callOnceCandidates">
    /// Optional. When supplied, any tool present in this set that survives the reserved-capability
    /// filter below has its NEW, governance-wrapped instance added too — see
    /// <see cref="WrapGoverned(IEnumerable{ProvisionedTool}, ConcurrentDictionary{AITool,byte}?)"/>'s
    /// remarks for why the wrap would otherwise sever reference-based candidate tracking.
    /// </param>
    private List<ProvisionedTool> FinalizeChain(
        List<ProvisionedTool> provisioned, string source, ConcurrentDictionary<AITool, byte>? callOnceCandidates = null)
    {
        var survivors = ReservedPlanCapabilityFilter.Exclude(provisioned.Select(p => p.Tool), source, _logger);
        var afterReservedFilter = KeepSurviving(provisioned, survivors);

        var wrapped = WrapGoverned(afterReservedFilter, callOnceCandidates);
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
    /// <remarks>
    /// <para>
    /// A tool whose <see cref="ProvisionedTool.McpServerName"/> is non-null — the provenance this
    /// builder already tracks positionally (see <see cref="ProvisionedTool"/>'s remarks) — is wrapped in
    /// <see cref="McpFailureNormalizingAIFunction"/> before <see cref="GovernedAIFunction"/>, so an MCP
    /// tool's non-throwing failure is normalized to the same <see cref="ConvertedToolFailure"/> marker
    /// every other tool source produces (see #468). <see cref="GovernedAIFunction"/> itself no longer
    /// needs to know or be told which source produced the tool it is wrapping.
    /// </para>
    /// <para>
    /// <strong>Deliberately narrow exception to "reference identity does not survive this wrap"</strong>
    /// (see <see cref="ProvisionedTool"/>'s remarks — that is why cross-skill provenance is tracked
    /// positionally, not by instance). When <paramref name="callOnceCandidates"/> is supplied and a
    /// pre-wrap tool is a member, the new wrapped instance is added too, so a caller checking
    /// membership against the FINAL published <see cref="AITool"/> list — after every later filter has
    /// run — still finds it. This is safe specifically because the set only ever GAINS an alias for an
    /// instance already inside it; it never lets a name substitute for a reference the way the general
    /// provenance problem above forbids.
    /// </para>
    /// </remarks>
    private List<AITool> WrapGoverned(
        IEnumerable<ProvisionedTool> provisioned, ConcurrentDictionary<AITool, byte>? callOnceCandidates = null)
    {
        var result = new List<AITool>();
        foreach (var p in provisioned)
        {
            var wrapped = p.Tool is AIFunction fn and not GovernedAIFunction
                ? new GovernedAIFunction(p.McpServerName is not null ? new McpFailureNormalizingAIFunction(fn) : fn)
                : p.Tool;

            if (callOnceCandidates is not null && callOnceCandidates.ContainsKey(p.Tool))
                callOnceCandidates.TryAdd(wrapped, 0);

            result.Add(wrapped);
        }

        return result;
    }

    /// <inheritdoc />
    public List<AITool> BuildToolsByName(IReadOnlyList<string> toolNames, string? agentName = null)
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
        var finalized = FinalizeChain(provisioned, "keyed-DI resolution by name").ConvertAll(p => p.Tool);

        // One of the three whole-agent-set exits — see ApplyCompositionTaint's remarks: a delegated
        // subagent's tool set is fully known only here, exactly like the merged path's is only known
        // after ResolveSurvivingTools.
        return ApplyCompositionTaint(finalized, agentName ?? "unknown-agent");
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
        var callOnceCandidates = new ConcurrentDictionary<AITool, byte>(ReferenceEqualityComparer.Instance);
        foreach (var skill in skills)
        {
            var skillTools = await BuildProvisionedToolsAsync(skill, options, callOnceCandidates, cancellationToken);
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

        var agentName = options.AgentNameOverride ?? skills.FirstOrDefault()?.Name ?? "unknown-agent";

        // Registration happens here, against the fully resolved, cross-skill-deduplicated,
        // AllowedTools-restricted surface — the true agent-level tool ceiling, and every skill's
        // candidates pooled into the one shared callOnceCandidates set above. See
        // RegisterSurvivingCallOnceTools's remarks for why per-skill registration cannot see the
        // cross-skill precedence/withholding ResolveSurvivingTools applies here.
        RegisterSurvivingCallOnceTools(deduplicated, callOnceCandidates, agentName);

        // One of the three whole-agent-set exits — see ApplyCompositionTaint's remarks. This is the
        // real, cross-skill tool set an agent runs with; a per-skill check earlier in this method's own
        // call chain (BuildProvisionedToolsAsync → BuildInjectedModeToolsAsync/BuildManagedModeToolsAsync
        // → FinalizeChain) would only ever see one skill's tools and could never confirm or rule out a
        // pairing that spans two skills — exactly the shape of the realistic exfiltration case (a
        // web-fetch skill plus an email skill on the same agent).
        var taintedTools = ApplyCompositionTaint(deduplicated, agentName);

        return new MergedToolChain(taintedTools, attributedMcp);
    }

    /// <summary>
    /// Runs tool-composition analysis over a whole agent's assembled tool set and re-wraps each
    /// implicated sink's <see cref="GovernedAIFunction"/> with the findings that name it, so
    /// <c>ToolInvocationGovernor</c> can enforce a RequireApproval posture at call time without needing
    /// to see the rest of the agent's tool set. Also reports the assessment through
    /// <see cref="ToolCompositionReporter"/> — build-time reporting and call-time enforcement are stamped
    /// from the exact same analysis run, so they can never describe two different tool sets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Runs at exactly three places: here, <see cref="BuildToolsAsync"/>, and
    /// <see cref="BuildToolsByName"/>.</strong> All three are genuine whole-agent-set exits — the point
    /// past which no more tools are added for that agent build. The per-skill
    /// <c>BuildInjectedModeToolsAsync</c>/<c>BuildManagedModeToolsAsync</c> that feed into
    /// <see cref="BuildToolsAsync"/> and this method are not: each only ever sees a fragment of the
    /// eventual set and is not a valid vantage point for a cross-skill composition check.
    /// </para>
    /// <para>
    /// <strong>Degrades to a no-op when analysis is unavailable</strong> (a null <c>_compositionAnalyzer</c>,
    /// the same optional-collaborator pattern this class already applies to <c>_surfaceScanner</c> and
    /// <c>_aiConfig</c>) — every production host registers the analyzer unconditionally, so this only
    /// matters for a test fixture that constructs <see cref="ToolChainBuilder"/> directly without it.
    /// </para>
    /// </remarks>
    private List<AITool> ApplyCompositionTaint(List<AITool> tools, string agentName)
    {
        if (_compositionAnalyzer is null)
            return tools;

        var assessment = _compositionAnalyzer.Analyze(tools);
        _compositionReporter?.Report(agentName, assessment);

        if (assessment.Findings.Count == 0)
            return tools;

        // Grouped so a sink implicated by more than one finding is re-wrapped exactly once, carrying
        // every finding that names it — never overwritten by a second, later finding for the same sink.
        var findingsBySink = new Dictionary<string, List<ToolCompositionFinding>>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in assessment.Findings)
        {
            if (!findingsBySink.TryGetValue(finding.SinkTool, out var forSink))
                findingsBySink[finding.SinkTool] = forSink = [];
            forSink.Add(finding);
        }

        return tools.Select(t =>
        {
            // Every tool reaching this point that can carry a taint is already a GovernedAIFunction —
            // WrapGoverned (via FinalizeChain, upstream of all three call sites) wraps every AIFunction
            // tool unconditionally. A tool that is not one (a rare non-AIFunction AITool) cannot carry a
            // taint and passes through unchanged; it is also, by construction, never a sink an operator
            // could have classified, since first-party capability declarations live on ITool, resolved
            // only for keyed-DI tools that ARE AIFunctions once converted.
            if (t is not GovernedAIFunction governed || !findingsBySink.TryGetValue(t.Name, out var findings))
                return t;

            // Re-wrap rather than mutate: the taint is set once, in the constructor, so carrying a
            // finding discovered by THIS analysis means unwrapping to the same inner function and
            // rewrapping — never double-governing, since InnerFunction always points at the real tool.
            // governed.Inner already carries any McpFailureNormalizingAIFunction wrapping intact —
            // there is no separate provenance flag left to forward.
            return (AITool)new GovernedAIFunction(
                governed.Inner, new ToolCompositionTaint(findings));
        }).ToList();
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
    /// keyed DI. Each task's resolved tools are folded into the caller's list sequentially, after every
    /// task has completed — the only shared, concurrently-written state is <paramref
    /// name="callOnceCandidates"/>, a <see cref="ConcurrentDictionary{TKey,TValue}"/> built specifically
    /// to be safe for exactly this: many <see cref="ProvisionToolAsync"/> tasks tagging candidates in
    /// parallel from <see cref="BuildProvisionedToolsAsync"/>'s <c>Task.WhenAll</c>.
    /// </summary>
    private async Task<List<ProvisionedTool>?> ProvisionToolAsync(
        Domain.AI.Tools.ToolDeclaration declaration,
        ConcurrentDictionary<AITool, byte> callOnceCandidates,
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

                    // A bundle-owned server's tools are published and governed under a namespaced name
                    // (never the bare, bundle-chosen one) so a malicious bundle cannot get a real host
                    // tool auto-granted by advertising a same-named tool of its own. See
                    // CapabilityEnvelope.IsBundleOwnedMcpServer and BundleOwnedMcpToolNaming.
                    var provisionedMcpTools = mcpTools
                        .Select(t => new ProvisionedTool(PublishServerTool(t, effectiveServerName, isBundleOwned), effectiveServerName))
                        .ToList();
                    TagCallOnceCandidates(declaration, provisionedMcpTools, callOnceCandidates);
                    return provisionedMcpTools;
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
        {
            var provisionedFirstParty = resolved.Select(t => new ProvisionedTool(t, null)).ToList();
            TagCallOnceCandidates(declaration, provisionedFirstParty, callOnceCandidates);
            return provisionedFirstParty;
        }

        if (declaration.HasFallback && !declaration.FallbackIsManual)
        {
            resolved = ResolveToolByName(declaration.Fallback!);
            if (resolved != null)
            {
                _logger.LogInformation("Using fallback tool {Fallback} for {ToolName}",
                    declaration.Fallback, declaration.Name);
                var provisionedFallback = resolved.Select(t => new ProvisionedTool(t, null)).ToList();
                TagCallOnceCandidates(declaration, provisionedFallback, callOnceCandidates);
                return provisionedFallback;
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
    /// Marks every tool in <paramref name="resolved"/> as a call-once CANDIDATE — not yet a
    /// registration — when <paramref name="declaration"/> was declared <c>CallOncePerConversation</c>.
    /// A no-op when the declaration was not (the common case).
    /// </summary>
    /// <remarks>
    /// Deliberately does not touch <see cref="IToolCallOncePolicy"/> directly — tagging only records
    /// "this instance MIGHT be registered later." <see cref="RegisterSurvivingCallOnceTools"/> performs
    /// the real registration, at a genuine whole-agent-set exit, against the tool's fully resolved,
    /// fully filtered final form — see that method's remarks for why every earlier point in this
    /// pipeline (including immediately after the plugin boundary) is unsafe to register from.
    /// </remarks>
    private static void TagCallOnceCandidates(
        Domain.AI.Tools.ToolDeclaration declaration,
        List<ProvisionedTool> resolved,
        ConcurrentDictionary<AITool, byte> callOnceCandidates)
    {
        if (!declaration.CallOncePerConversation)
            return;

        foreach (var provisioned in resolved)
            callOnceCandidates.TryAdd(provisioned.Tool, 0);
    }

    /// <summary>
    /// Registers every tool in <paramref name="survivors"/> that <see cref="TagCallOnceCandidates"/>
    /// tagged as a call-once candidate under the call-once policy. A no-op when no
    /// <see cref="IToolCallOncePolicy"/> was injected (a caller that constructed this builder without
    /// one — matches every other optional governance dependency here).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Callable only from a genuine whole-agent-set exit — <see cref="BuildToolsAsync"/> or
    /// <see cref="BuildMergedToolsWithSourcesAsync"/> — never from a per-skill resolution method, and
    /// never before <c>ResolveSurvivingTools</c> (<c>ToolChainBuilder.Surface.cs</c>) has run.</strong>
    /// An earlier revision registered right after <see cref="ApplyPluginBoundaryIfPluginSkill"/>, inside
    /// <see cref="BuildManagedModeToolsAsync"/> — closing the plugin-boundary bypass (a denied tool could
    /// no longer poison the policy) but missing two LATER filters that also run in the real pipeline:
    /// <see cref="ReservedPlanCapabilityFilter.Exclude"/> (inside <see cref="FinalizeChain"/>) and, for
    /// any multi-skill agent, <c>ResolveSurvivingTools</c>'s cross-skill first-party precedence and
    /// drift/collision withholding. A hostile MCP server whose bare-named tool loses that cross-skill
    /// precedence fight is discarded from the published surface exactly like a plugin-denied tool is —
    /// but the earlier per-skill registration point could not see that discard, because it happens in a
    /// different method entirely, after this one had already returned. Registering here, against
    /// <paramref name="survivors"/> — the actual, final, cross-skill-deduplicated,
    /// <c>AllowedTools</c>-restricted set an agent runs with — closes all of these at once, by
    /// construction, rather than by chasing each filter with its own membership check.
    /// </para>
    /// <para>
    /// <strong>Reference-based, not name-based, and deliberately so.</strong> A hostile MCP server could
    /// declare a tool sharing a REAL first-party tool's name; if this checked <paramref name="survivors"/>
    /// by name alone, the surviving first-party tool would be wrongly registered call-once on behalf of
    /// a declaration that never had authority over it — reintroducing the exact class of defect
    /// <see cref="ProvisionedTool"/>'s own remarks warn against for provenance tracking generally. Instead,
    /// <paramref name="callOnceCandidates"/> is populated with the ORIGINAL resolved instance in
    /// <see cref="TagCallOnceCandidates"/>, and <see cref="WrapGoverned(IEnumerable{ProvisionedTool},ConcurrentDictionary{AITool,byte}?)"/>
    /// carries that membership forward onto the new <see cref="GovernedAIFunction"/> instance as each
    /// tool is wrapped — so checking <paramref name="survivors"/> by reference here still correctly
    /// distinguishes "the specific instance a call-once declaration actually produced" from "any tool
    /// that happens to share its published name."
    /// </para>
    /// <para>
    /// Registers by the tool's own resolved <see cref="AITool.Name"/>, not the originating
    /// <see cref="Domain.AI.Tools.ToolDeclaration.Name"/> — the two diverge for an MCP server resolution
    /// (the declaration names the server; each returned tool keeps its own name, possibly namespaced for
    /// a bundle-owned server) and for a fallback resolution (the declaration names the primary tool; the
    /// resolved tool is the fallback). This is the same name
    /// <see cref="Interfaces.Governance.ToolCallAdmissionRequest.ToolName"/> carries at invocation time,
    /// which is what the call-once gate actually checks against.
    /// </para>
    /// <para>
    /// <strong>Registration is process-global by tool name, not scoped to <paramref name="contextLabel"/>.
    /// </strong> <see cref="IToolCallOncePolicy"/> answers "was this name EVER declared call-once by ANY
    /// skill" — it has no way to answer "call-once for skill X but not skill Y", because
    /// <see cref="Interfaces.Governance.ToolCallAdmissionRequest"/> itself carries no skill identity for
    /// the check to key on. This matches how <c>ToolBehaviorRegistry</c> already treats a tool name as a
    /// global identifier for a single capability, and is the correct reading for the common case (a
    /// first-party keyed-DI tool has exactly one registration in the process). It is the WRONG reading if
    /// two unrelated skills happen to resolve a same-named tool with genuinely different call-once intent
    /// — <paramref name="contextLabel"/> exists so that case can at least be logged, not silently
    /// misapplied (the skill id for a single-skill build, the agent name for a merged one — there is no
    /// single skill to blame once multiple skills' tools have been merged). Properly scoping this would
    /// mean threading skill or agent identity through the whole admission chain, which is a larger,
    /// separate change.
    /// </para>
    /// </remarks>
    private void RegisterSurvivingCallOnceTools(
        IEnumerable<AITool> survivors, ConcurrentDictionary<AITool, byte> callOnceCandidates, string contextLabel)
    {
        if (_callOncePolicy is null || callOnceCandidates.IsEmpty)
            return;

        foreach (var tool in survivors)
        {
            if (!callOnceCandidates.ContainsKey(tool))
                continue;

            if (_callOncePolicy.IsCallOnce(tool.Name))
            {
                _logger.LogWarning(
                    "Tool {ToolName} was already registered call-once (by an earlier build) before " +
                    "{ContextLabel} declared it call-once too. Call-once enforcement is process-global by " +
                    "tool name — if these are genuinely different tools that happen to share a name, or if " +
                    "only one build actually intends call-once semantics, this will over-restrict the other.",
                    tool.Name, contextLabel);
            }

            _callOncePolicy.Register(tool.Name);
        }
    }

    /// <summary>
    /// Resolves the MCP tools to inject for an Injected-mode skill. Off the bundle path this is every
    /// configured server's tools (the historical behaviour). On a bundle run it contacts <em>only</em> the
    /// servers the caller's envelope grants — never enumerating every host server — so an ungranted server
    /// is never reached at all (no side-effect connection, no tool-schema disclosure), closing
    /// SSRF-by-construction. An empty grant yields no MCP tools.
    /// </summary>
    /// <param name="envelope">
    /// The ambient <see cref="CapabilityEnvelopeAccessor.Current"/>, read once by the caller and passed in
    /// rather than re-read here — both this method and the caller's own bundle-ownership check need it.
    /// </param>
    private async Task<IReadOnlyList<(string ServerName, IList<AITool> Tools)>> ResolveInjectedMcpToolsAsync(
        Domain.AI.Bundles.CapabilityEnvelope? envelope, CancellationToken cancellationToken)
    {
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
    /// name (e.g. <c>"epr-mcp"</c>) on the managed resolution path, and whether that resolved server is
    /// this run's own bundle-owned one — the caller uses that flag to decide whether the resolved tools
    /// must be published under a namespaced name (see <see cref="BundleOwnedMcpToolNaming"/>).
    /// </summary>
    /// <remarks>
    /// Off the bundle path no envelope is published, so the declared name passes through unchanged
    /// (<c>IsBundleOwned: false</c>) and every server is permitted. On a bundle run, an exact grant for
    /// the declared name wins outright — a host-configured, non-namespaced server is unaffected by the
    /// fallback below. A skill author cannot know their bundle's future id at authoring time, so a
    /// bundle's own server is granted under a namespaced key (<c>{bundleId}:{declaredName}</c>) that never
    /// exact-matches the declaration; when the armed envelope contains exactly one grant ending in
    /// <c>:{declaredName}</c>, that full namespaced name is resolved to contact. When more than one grant
    /// shares the suffix, the bundle-owned one wins if exactly one of the matches is bundle-owned
    /// (safe only because every skill resolved during a bundle run is one of that bundle's own — see the
    /// resolution-time comment below); any other multi-match shape is still ambiguous and denied rather
    /// than guessed. <c>IsBundleOwned</c> is decided from
    /// <see cref="Domain.AI.Bundles.CapabilityEnvelope.IsBundleOwnedMcpServer"/> — the run's own authoritative record,
    /// itself populated by a single writer (<c>RunBundleCommandHandler.WithBundleOwnedMcpServers</c>) —
    /// never from the suffix match alone: a host-installed plugin's own MCP server is namespaced under
    /// the identical <c>{Prefix}:{ServerName}</c> shape, so a suffix match can legitimately resolve to an
    /// explicitly-granted plugin server that is NOT bundle-owned and must NOT be renamed. Returns a
    /// <see langword="null"/> server name when nothing resolves, logged and never contacted — a bundle can
    /// never reach a host MCP server it was not granted.
    /// </remarks>
    private (string? ServerName, bool IsBundleOwned) ResolveEffectiveMcpServerName(string declaredName)
    {
        var envelope = CapabilityEnvelopeAccessor.Current;
        if (envelope is null || envelope.GrantsMcpServer(declaredName))
            return (declaredName, false);

        var suffix = ":" + declaredName;
        var matches = envelope.AllowedMcpServers
            .Where(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
            return (matches[0], envelope.IsBundleOwnedMcpServer(matches[0]));

        // More than one namespaced grant shares this bare server name — an unrelated host- or
        // plugin-granted server (see CapabilityEnvelope.BundleOwnedMcpServers remarks: both use the
        // identical {Prefix}:{ServerName} shape) coincidentally ends in the same suffix as THIS run's own
        // bundle-owned server. Every skill resolved while running a bundle is one of that bundle's OWN
        // OwnedSkills — OverlayAwareAgentOwnedSkillStore is authoritative for the ephemeral agent and never
        // falls through to a caller's other skills — so a declaredName reaching this method during a
        // bundle run can only ever mean the bundle's own server, never the unrelated grant it happens to
        // collide with. Preferring the bundle-owned candidate therefore only makes the bundle's own
        // already-granted access reliable against an incidental name clash; it can never resolve to the
        // caller's separate grant instead, so it cannot escalate what this run can reach. Staging rejects
        // duplicate server names within one bundle's own manifest set, so at most one match here is ever
        // bundle-owned.
        var bundleOwnedMatches = matches.Where(envelope.IsBundleOwnedMcpServer).ToList();
        if (bundleOwnedMatches.Count == 1)
            return (bundleOwnedMatches[0], true);

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
