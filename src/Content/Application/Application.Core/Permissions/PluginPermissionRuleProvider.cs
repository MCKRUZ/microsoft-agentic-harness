using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.Common.Helpers;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.Core.Permissions;

/// <summary>
/// Emits <see cref="ToolPermissionRule"/> entries derived from plugin declarations that
/// specify an autonomy level override. Rules feed into the existing 3-phase permission
/// resolver alongside agent-level autonomy tier rules.
/// </summary>
/// <remarks>
/// <para>
/// Any <c>DeniedTools</c> declared on a loaded plugin emit Deny rules at a lower priority value
/// (checked first) and are marked <c>IsBypassImmune</c> so they cannot be overridden by
/// auto-approve modes. These deny rules are emitted <b>regardless</b> of whether the plugin also
/// sets an <c>AutonomyLevel</c> — the deny boundary is independent of the autonomy override.
/// </para>
/// <para>
/// A plugin's <c>AutonomyLevel</c> is enforced as an <em>authoritative baseline</em> scoped to the
/// plugin's <b>real, declared tool names</b> — never a synthetic <c>{plugin}:*</c> wildcard, which no
/// live tool name ever matches. The provider enumerates every skill attributed to the plugin (via
/// <see cref="SkillDefinition.PluginSource"/>) and collects the tool names those skills declare
/// (<c>AllowedTools</c>, <c>ToolDeclarations</c>, and any pre-created <c>Tools</c>). Each distinct name
/// gets one rule flagged <see cref="ToolPermissionRule.IsAuthoritativeBaseline"/>, so the resolver
/// applies the plugin's autonomy in both directions (Autonomous → Allow can loosen a stricter
/// default; Restricted/Supervised → Ask can tighten) while a bypass-immune <c>DeniedTools</c> rule
/// still wins.
/// </para>
/// <para>
/// <b>Own-surface constraint (security).</b> The baseline only covers names that are genuinely part of
/// the plugin's own tool surface — tools it contributes (MCP or skill-provided). A name that resolves
/// to a globally-registered keyed-DI tool (the shared, powerful harness tools such as
/// <c>file_system</c> or a shell) is <em>not</em> owned by the plugin and is excluded from the
/// baseline (with a warning), even if the plugin's <c>SKILL.md</c> names it. Otherwise an operator who
/// marks a third-party plugin <c>Autonomous</c> — intending to auto-run that plugin's own narrow tools
/// — would silently auto-approve a powerful global tool agent-wide (for every caller, not just the
/// plugin). The plugin can still <em>use</em> such a tool; it just does not get to auto-approve it.
/// Bypass-immune <c>DeniedTools</c> remains the backstop for sensitive global tools.
/// </para>
/// <para>
/// <b>Limitation.</b> A plugin whose skills declare no tools (Injected mode — the skill receives all
/// MCP tools at runtime) exposes no statically-enumerable tool names, so its autonomy baseline cannot
/// be scoped to specific tools and is skipped with a warning. Operators who need an autonomy baseline
/// on such a plugin must name its tools via the plugin's <c>AllowedTools</c> or per-skill
/// <c>allowed-tools</c> declarations. (<c>DeniedTools</c> are unaffected — they name tools explicitly.)
/// </para>
/// <para>
/// <b>Unverified boundary (#524 round-2 code-review).</b> This provider's Deny rules trust a plugin's
/// <c>DeniedTools</c> entries exactly the way <c>ToolChainBuilder</c> used to before #524 — an entry
/// naming no real tool is a silent no-op, and this is the ONE enforcement path #524's tool-SET
/// filtering never reaches: <c>DeniedTools</c> exists specifically to let a plugin block a *global*
/// tool it does not own (the doc above's "backstop for sensitive global tools"), and
/// <c>ToolChainBuilder.ApplyPluginBoundaryIfPluginSkill</c> only ever filters the tool SET sourced from
/// the plugin's OWN skill — a global tool reachable through any OTHER skill in the same agent is never
/// touched by that filter regardless of this plugin's boundary state. When
/// <see cref="IPluginRegistry.GetBoundaryStatus"/> is not <see cref="PluginBoundaryStatus.Verified"/>
/// for a plugin, this provider therefore ALSO emits a bypass-immune Deny rule for every first-party
/// tool name known to the host (<see cref="FirstPartyToolLookup.RegisteredFirstPartyToolKeys"/>) — not
/// scoped to this plugin, because a corrupted/unresolved entry gives no way to know which specific
/// global tool it was meant to protect. This is agent-wide and deliberately broad: Matt's explicit call
/// (this PR's own review) was that the collateral cost of over-blocking shared tools is acceptable
/// against the alternative of leaving a sensitive tool's only declared protection silently absent. The
/// existing, correctly-scoped DeniedTools and autonomy-baseline rules below are still emitted
/// unconditionally alongside this — harmless overlap for first-party names, and still the only
/// coverage for any additional, validly-named non-first-party (MCP) entry in the same list.
/// </para>
/// </remarks>
public sealed class PluginPermissionRuleProvider : IPermissionRuleProvider
{
    private readonly IPluginRegistry _registry;
    private readonly ISkillMetadataRegistry _skillRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly FirstPartyToolLookup _firstPartyToolLookup;
    private readonly ILogger<PluginPermissionRuleProvider> _logger;

    // #611: GetRulesAsync is called fresh on every tool-permission resolution
    // (ThreePhasePermissionResolver.CollectRulesAsync), and #612 makes it construct first-party
    // tools (see TryResolvePublishedName) to learn a name that can disagree with its DI key — doing
    // that unconditionally on every call turns an unverified-boundary state into unbounded repeated
    // construction cost for as long as it persists. Cache the computed rule list, keyed on
    // IPluginRegistry.StateVersion, so recomputation happens only when something that could change
    // the result actually did.
    //
    // Correctness rests on two inputs to ComputeRules that are NOT covered by StateVersion, because
    // both are immutable after this instance is constructed: FirstPartyToolLookup's registered-key
    // set is built once at DI registration time, and ISkillMetadataRegistry has exactly one
    // implementation (SkillMetadataRegistry), whose load is one-shot with no invalidation or
    // config-change hook. If either ever gains a runtime-mutation path, this cache must be keyed on
    // that too, or it will silently serve a stale autonomy baseline / tool-name resolution.
    private readonly Lock _cacheLock = new();
    private long _cachedVersion = -1;
    private IReadOnlyList<ToolPermissionRule>? _cachedRules;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginPermissionRuleProvider"/> class.
    /// </summary>
    /// <param name="registry">The plugin registry providing loaded plugin metadata.</param>
    /// <param name="skillRegistry">
    /// The skill metadata registry, used to enumerate the tools declared by a plugin's skills so the
    /// autonomy baseline can be scoped to real tool names.
    /// </param>
    /// <param name="serviceProvider">
    /// Used to detect globally-registered keyed-DI tools so the autonomy baseline can exclude shared
    /// harness tools the plugin does not own.
    /// </param>
    /// <param name="firstPartyToolLookup">
    /// Supplies every known first-party tool name for the unverified-boundary fail-closed response —
    /// see this type's remarks.
    /// </param>
    /// <param name="logger">Logger for invalid autonomy level and unscoped-baseline warnings.</param>
    public PluginPermissionRuleProvider(
        IPluginRegistry registry,
        ISkillMetadataRegistry skillRegistry,
        IServiceProvider serviceProvider,
        FirstPartyToolLookup firstPartyToolLookup,
        ILogger<PluginPermissionRuleProvider> logger)
    {
        _registry = registry;
        _skillRegistry = skillRegistry;
        _serviceProvider = serviceProvider;
        _firstPartyToolLookup = firstPartyToolLookup;
        _logger = logger;
    }

    /// <inheritdoc />
    public PermissionRuleSource Source => PermissionRuleSource.PluginDeclaration;

    /// <inheritdoc />
    public Task<IReadOnlyList<ToolPermissionRule>> GetRulesAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        // The rule set does not depend on agentId (every provider call site below is agent-agnostic),
        // so a single cache slot keyed only on registry state is correct for every caller.
        var version = _registry.StateVersion;

        lock (_cacheLock)
        {
            if (_cachedRules is not null && _cachedVersion == version)
                return Task.FromResult(_cachedRules);
        }

        var rules = ComputeRules();

        lock (_cacheLock)
        {
            _cachedRules = rules;
            _cachedVersion = version;
        }

        return Task.FromResult(rules);
    }

    private IReadOnlyList<ToolPermissionRule> ComputeRules()
    {
        var rules = new List<ToolPermissionRule>();
        var anyBoundaryUnverified = false;

        foreach (var plugin in _registry.GetLoadedPlugins())
            anyBoundaryUnverified |= EmitPluginRules(plugin, rules);

        // #524 round-2 code-review: at least one plugin's boundary can't be trusted (an
        // AllowedTools/DeniedTools entry matches no real tool), and this class's own DeniedTools
        // rules — the ONLY enforcement path that protects a global tool a plugin does not own — trust
        // those entries exactly the way ToolChainBuilder used to before #524, with no existence check.
        // Which specific global tool a corrupted entry was meant to protect is unknowable, so the
        // fail-closed response is broad, not scoped to the one plugin: deny every known first-party
        // tool agent-wide until every plugin's boundary is Verified. See this type's remarks.
        if (anyBoundaryUnverified)
            EmitUnverifiedBoundaryFailClosedRules(rules);

        return rules;
    }

    /// <summary>
    /// Emits <paramref name="plugin"/>'s own Deny and autonomy-baseline rules into
    /// <paramref name="rules"/>, and reports whether its boundary is unverified (contributing to
    /// <see cref="ComputeRules"/>'s agent-wide fail-closed decision).
    /// </summary>
    private bool EmitPluginRules(LoadedPlugin plugin, List<ToolPermissionRule> rules)
    {
        // #613: IPluginRegistry.GetLoadedPlugins() returns every REGISTERED plugin regardless of
        // status, despite the name — Disabled and Failed plugins are included. Only Status ==
        // Loaded plugins are ever fed to PluginToolBoundaryTracker.Seed
        // (PluginToolBoundaryStartupValidator.StartAsync filters before calling it), so a
        // Disabled/Failed plugin is never seeded and GetBoundaryStatus's default for it is now
        // Pending (#613's fix for the real startup race — see that method's remarks). Such a
        // plugin contributes zero tools and has no boundary to distrust; without this guard,
        // registering any disabled or failed plugin — a normal operational state — would flip
        // the agent-wide fail-closed response and silently deny every first-party tool for the
        // process lifetime.
        var boundaryUnverified = plugin.Status == PluginLoadStatus.Loaded
            && _registry.GetBoundaryStatus(plugin.Name) != PluginBoundaryStatus.Verified;

        // DeniedTools are bypass-immune and enforced independently of any AutonomyLevel:
        // a plugin that only denies tools (no autonomy override) must still contribute its
        // Deny rules. Emitted first so the boundary applies even when AutonomyLevel is unset
        // or invalid.
        if (plugin.Declaration.DeniedTools is { Count: > 0 } denied)
            foreach (var deniedTool in denied)
                AddDenyRuleWithPublishedNameCoverage(rules, deniedTool);

        if (string.IsNullOrEmpty(plugin.Declaration.AutonomyLevel))
            return boundaryUnverified;

        // Name-only: the declaration is authored in a plugin manifest, outside this repo. A
        // numeric AutonomyLevel would map straight into the tier-to-behavior conversion below
        // and set the plugin's baseline to a tier nobody declared.
        if (!EnumNameHelper.TryParseName<AutonomyLevel>(plugin.Declaration.AutonomyLevel, out var autonomyLevel))
        {
            _logger.LogWarning(
                "Plugin {Name}: invalid AutonomyLevel '{Level}', skipping baseline governance rule",
                plugin.Name, plugin.Declaration.AutonomyLevel);
            return boundaryUnverified;
        }

        // Both Restricted and Supervised map to Ask — differentiation is via per-tool
        // overrides in AutonomyTierRuleProvider config, not at the plugin boundary. Shared mapping so
        // the plugin and capability-envelope providers cannot drift on the tier-to-behavior rule.
        var defaultBehavior = autonomyLevel.ToDefaultPermissionBehavior();

        var pluginToolNames = EnumeratePluginToolNames(plugin.Name);
        if (pluginToolNames.Count == 0)
        {
            _logger.LogWarning(
                "Plugin {Name}: AutonomyLevel '{Level}' set, but the plugin's skills declare no tool names " +
                "(Injected mode) — the autonomy baseline cannot be scoped to specific tools and is skipped. " +
                "Declare the tools via the plugin's AllowedTools or a skill's allowed-tools to enforce it.",
                plugin.Name, plugin.Declaration.AutonomyLevel);
            return boundaryUnverified;
        }

        foreach (var toolName in pluginToolNames)
        {
            rules.Add(new ToolPermissionRule(
                toolName,
                null,
                defaultBehavior,
                PermissionRuleSource.PluginDeclaration,
                Priority: 5,
                IsAuthoritativeBaseline: true));
        }

        return boundaryUnverified;
    }

    /// <summary>
    /// Emits the agent-wide fail-closed Deny rule for every known first-party tool — see
    /// <see cref="ComputeRules"/>'s call site for why this fires, and <see cref="EmitPluginRules"/>
    /// for the per-plugin rules it supplements.
    /// </summary>
    /// <remarks>
    /// #612 code-review: deliberately key-only here, NOT <see cref="AddDenyRuleWithPublishedNameCoverage"/>.
    /// That helper resolves (constructs) each tool to compare its published name against the key —
    /// proportionate for <see cref="EmitPluginRules"/>'s DeniedTools loop (a small,
    /// explicitly-authored list), but this loop iterates EVERY registered first-party tool: doing the
    /// same resolution here would construct the host's entire tool set as a side effect of a
    /// permission check whenever any plugin's boundary is merely unverified, not because anything
    /// actually invoked those tools. Accepted trade-off (Matt's explicit call): this fallback keeps a
    /// narrower residual gap — a first-party tool whose published name disagrees with its key would
    /// still evade this blanket deny under that name — in exchange for never running arbitrary tool
    /// construction as a side effect of this check. No tool in this codebase has that divergence
    /// today (see <c>ToolCatalogTests.Catalog_ToolWhoseNameDisagreesWithItsKey_...</c> for the one
    /// place it's exercised, deliberately synthetic). <see cref="EmitPluginRules"/>'s DeniedTools loop
    /// still closes the gap for the actually-reported, common case: a plugin's own DeniedTools entry.
    /// </remarks>
    private void EmitUnverifiedBoundaryFailClosedRules(List<ToolPermissionRule> rules)
    {
        foreach (var toolName in _firstPartyToolLookup.RegisteredFirstPartyToolKeys)
            rules.Add(DenyRule(toolName));
    }

    /// <summary>
    /// Adds a bypass-immune Deny rule for <paramref name="toolKey"/>, and — when it resolves to a
    /// real first-party tool whose self-reported name disagrees with the key — a second Deny rule
    /// against that resolved name. Used only by the per-plugin <c>DeniedTools</c> loop above, where
    /// the set of tools to resolve is small and explicitly authored by the plugin manifest — see the
    /// unverified-boundary fail-closed loop's own comment for why it deliberately does NOT call this.
    /// </summary>
    private void AddDenyRuleWithPublishedNameCoverage(List<ToolPermissionRule> rules, string toolKey)
    {
        rules.Add(DenyRule(toolKey));

        if (TryResolvePublishedName(toolKey, out var publishedName) && publishedName != toolKey)
            rules.Add(DenyRule(publishedName));
    }

    /// <summary>
    /// Resolves <paramref name="toolKey"/>'s converted, self-reported <see cref="ITool.Name"/> —
    /// the value the runtime permission resolver (<c>ThreePhasePermissionResolver.Matches</c>,
    /// Infrastructure.AI) actually matches a Deny rule's pattern against at invocation, which can
    /// legitimately disagree with the DI registration key a plugin's <c>DeniedTools</c> entry names.
    /// Returns <see langword="false"/>, with <paramref name="publishedName"/> set to
    /// <paramref name="toolKey"/> itself, when the key names no known first-party tool OR
    /// constructing it throws.
    /// </summary>
    /// <remarks>
    /// A first-party tool can require a dependency this particular host never wired (the same
    /// failure mode that made an earlier, unconditional "construct every registered tool" attempt at
    /// #524's existence check break host boot) — <see cref="FirstPartyToolLookup.TryResolve"/> guards
    /// against exactly that, catching and reporting a construction failure instead of propagating it,
    /// so one broken entry in a plugin's <c>DeniedTools</c> can't take down permission-rule
    /// computation for every tool call. The key-pattern Deny rule for the affected tool still gets
    /// emitted by the caller regardless of this method's outcome. A failure here is caught inside
    /// <see cref="ComputeRules"/>, so its result — the narrower, key-only coverage — is what gets
    /// cached: a tool whose constructor throws stays uncovered by the published-name rule for as long
    /// as the cached result stands (until the next <see cref="IPluginRegistry"/> mutation triggers a
    /// recompute), not retried on every call. A dependency-not-wired failure is deterministic, so a
    /// retry would fail identically anyway.
    /// </remarks>
    private bool TryResolvePublishedName(string toolKey, out string publishedName)
    {
        var tool = _firstPartyToolLookup.TryResolve(toolKey, out var constructionError);
        if (tool is not null)
        {
            publishedName = tool.Name;
            return true;
        }

        if (constructionError is not null)
        {
            // Error, not Warning: this is a bypass-immune security control (a plugin's DeniedTools
            // backstop) now only partially enforced for this one tool — a level that gets filtered
            // out of most production log configurations is the wrong fit for that.
            _logger.LogError(constructionError,
                "Could not construct first-party tool '{ToolKey}' to learn its published name for a " +
                "plugin-boundary Deny rule — the key-pattern rule for it still applies, but a caller " +
                "invoking it under a self-reported name that disagrees with the key would not be covered.",
                toolKey);
        }

        publishedName = toolKey;
        return false;
    }

    /// <summary>
    /// A bypass-immune Deny rule for <paramref name="toolName"/> — the identical shape both the
    /// per-plugin <c>DeniedTools</c> loop and the unverified-boundary fail-closed response above need.
    /// </summary>
    private static ToolPermissionRule DenyRule(string toolName) => new(
        toolName,
        null,
        PermissionBehaviorType.Deny,
        PermissionRuleSource.PluginDeclaration,
        Priority: 1,
        IsBypassImmune: true);

    /// <summary>
    /// Collects the distinct tool names declared by every skill attributed to
    /// <paramref name="pluginName"/>, drawn from each skill's <see cref="SkillDefinition.AllowedTools"/>,
    /// <see cref="SkillDefinition.ToolDeclarations"/>, and pre-created <see cref="SkillDefinition.Tools"/>.
    /// Names are matched at invocation against the live tool set, so they mirror the names the agent
    /// actually calls. Names that are <em>not</em> part of the plugin's own tool surface — i.e. tools
    /// that resolve to a globally-registered keyed-DI tool — are excluded so the autonomy baseline
    /// cannot loosen a shared harness tool the plugin does not own (see the type remarks).
    /// </summary>
    private IReadOnlyCollection<string> EnumeratePluginToolNames(string pluginName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in _skillRegistry.GetAll())
        {
            if (!string.Equals(skill.PluginSource, pluginName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (skill.AllowedTools is { Count: > 0 } allowed)
                foreach (var name in allowed)
                    AddIfOwned(names, name, pluginName);

            if (skill.ToolDeclarations is { Count: > 0 } declarations)
                foreach (var declaration in declarations)
                    AddIfOwned(names, declaration.Name, pluginName);

            if (skill.Tools is { Count: > 0 } tools)
                foreach (var tool in tools)
                    AddIfOwned(names, tool.Name, pluginName);
        }

        return names;
    }

    /// <summary>
    /// Adds <paramref name="name"/> to the baseline set only when it is a non-empty name genuinely
    /// owned by the plugin. A name that resolves to a globally-registered keyed-DI tool is a shared
    /// harness tool the plugin does not own; it is skipped with a warning so the plugin's autonomy
    /// baseline can never auto-approve it agent-wide.
    /// </summary>
    private void AddIfOwned(HashSet<string> names, string? name, string pluginName)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (_serviceProvider.GetKeyedService<ITool>(name) is not null)
        {
            _logger.LogWarning(
                "Plugin {Name}: tool '{Tool}' named in the plugin's skills is a global keyed-DI tool the plugin " +
                "does not own — excluded from the plugin's autonomy baseline. Use a per-tool AutonomyTier override " +
                "or DeniedTools to govern shared tools.",
                pluginName, name);
            return;
        }

        names.Add(name);
    }
}
