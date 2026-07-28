using System.Diagnostics;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Permissions;
using Domain.AI.Permissions;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Permissions;

/// <summary>
/// Implements the 3-phase tool permission resolution algorithm.
/// </summary>
/// <remarks>
/// <para>Resolution phases:</para>
/// <list type="number">
///   <item><description><strong>Phase 0 (Rate Limit)</strong>: Check denial tracker for auto-deny before any rule evaluation.</description></item>
///   <item><description><strong>Phase 1 (Deny/Safety)</strong>: Check safety gates first, then find the first matching Deny rule.</description></item>
///   <item><description><strong>Phase 1.5 (Authoritative baseline)</strong>: Arbitrate the rules flagged <see cref="ToolPermissionRule.IsAuthoritativeBaseline"/> by specificity, then restrictiveness.</description></item>
///   <item><description><strong>Phase 2 (Ask)</strong>: Find the first matching Ask rule. If bypass-immune, return Ask regardless.</description></item>
///   <item><description><strong>Phase 3 (Allow)</strong>: Find the first matching Allow rule. If no match, default to Ask.</description></item>
/// </list>
/// <para>Rules are sorted by <see cref="ToolPermissionRule.Priority"/> ascending before evaluation.</para>
/// <para>
/// Authoritative baselines are resolved <em>only</em> in phase 1.5 — the phase-ordered scans skip them.
/// That separation is what lets a rule provider close its own allowlist: it can emit per-name baseline
/// grants alongside a catch-all baseline Deny, and the specificity arbitration in phase 1.5 gives the
/// grants precedence while any name they do not cover falls to the Deny. Were baselines also visible to
/// the phase-ordered scans, the catch-all Deny would match in phase 1b and deny the granted names too.
/// </para>
/// </remarks>
public sealed class ThreePhasePermissionResolver : IToolPermissionService
{
    private readonly IEnumerable<IPermissionRuleProvider> _ruleProviders;
    private readonly ISafetyGateRegistry _safetyGateRegistry;
    private readonly IPatternMatcher _patternMatcher;
    private readonly IDenialTracker _denialTracker;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<ThreePhasePermissionResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreePhasePermissionResolver"/> class.
    /// </summary>
    /// <param name="ruleProviders">All registered rule providers, aggregated for resolution.</param>
    /// <param name="safetyGateRegistry">Registry of bypass-immune safety gates.</param>
    /// <param name="patternMatcher">Pattern matcher for tool name and operation matching.</param>
    /// <param name="denialTracker">Tracks repeated denials for rate-limiting auto-deny.</param>
    /// <param name="options">Configuration monitor providing the denial rate limit threshold.</param>
    /// <param name="logger">Logger for permission resolution tracing.</param>
    public ThreePhasePermissionResolver(
        IEnumerable<IPermissionRuleProvider> ruleProviders,
        ISafetyGateRegistry safetyGateRegistry,
        IPatternMatcher patternMatcher,
        IDenialTracker denialTracker,
        IOptionsMonitor<AppConfig> options,
        ILogger<ThreePhasePermissionResolver> logger)
    {
        _ruleProviders = ruleProviders;
        _safetyGateRegistry = safetyGateRegistry;
        _patternMatcher = patternMatcher;
        _denialTracker = denialTracker;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<PermissionDecision> ResolvePermissionAsync(
        string agentId,
        string toolName,
        string? operation = null,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        // Phase 0: Rate-limit check — auto-deny tools that have been denied too many times
        if (_denialTracker.IsRateLimited(agentId, toolName, operation))
        {
            var threshold = _options.CurrentValue.AI.Permissions.DenialRateLimitThreshold;
            var rateLimitDecision = PermissionDecision.Deny(
                $"Tool '{toolName}' has been denied {threshold} times. Auto-denied by rate limiter.");

            LogDecision(agentId, toolName, rateLimitDecision);
            return rateLimitDecision;
        }

        // Phase 1a: Safety gates (bypass-immune, checked before any rules)
        var triggeredGate = _safetyGateRegistry.CheckSafetyGate(toolName, parameters);
        if (triggeredGate is not null)
        {
            var gateDecision = PermissionDecision.Ask(
                $"Safety gate triggered: {triggeredGate.Description} (path pattern: '{triggeredGate.PathPattern}')");

            LogDecision(agentId, toolName, gateDecision);
            return gateDecision;
        }

        // Collect and sort all rules from all providers
        var allRules = await CollectRulesAsync(agentId, cancellationToken);
        var sortedRules = allRules.OrderBy(r => r.Priority).ToList();

        // Phase 1b: Deny rules
        var denyRule = FindFirstMatchingRule(sortedRules, toolName, operation, PermissionBehaviorType.Deny);
        if (denyRule is not null)
        {
            var denyDecision = PermissionDecision.Deny(
                $"Denied by rule from {denyRule.Source} (pattern: '{denyRule.ToolPattern}').",
                denyRule);

            LogDecision(agentId, toolName, denyDecision);
            return denyDecision;
        }

        // Phase 1.5: Authoritative baseline (e.g. a plugin's AutonomyLevel scoped to its own tools).
        // Runs after Deny so a bypass-immune DeniedTools rule or a safety gate still wins, but before
        // Ask/Allow phase ordering so an operator-set per-plugin baseline takes precedence over the
        // generic tier/default rules in BOTH directions (Allow can loosen, Ask can tighten). Ordinary
        // rules never set this flag, so this phase is a no-op for every non-plugin deployment.
        var baselineRule = FindFirstAuthoritativeBaseline(sortedRules, toolName, operation);
        if (baselineRule is not null)
        {
            var baselineDecision = new PermissionDecision(
                baselineRule.Behavior,
                $"Authoritative baseline from {baselineRule.Source} (pattern: '{baselineRule.ToolPattern}').",
                baselineRule,
                baselineRule.Source);

            LogDecision(agentId, toolName, baselineDecision);
            return baselineDecision;
        }

        // Phase 2: Ask rules
        var askRule = FindFirstMatchingRule(sortedRules, toolName, operation, PermissionBehaviorType.Ask);
        if (askRule is not null)
        {
            var askDecision = PermissionDecision.Ask(
                $"Confirmation required by rule from {askRule.Source} (pattern: '{askRule.ToolPattern}').",
                askRule);

            LogDecision(agentId, toolName, askDecision);
            return askDecision;
        }

        // Phase 3: Allow rules
        var allowRule = FindFirstMatchingRule(sortedRules, toolName, operation, PermissionBehaviorType.Allow);
        if (allowRule is not null)
        {
            var allowDecision = PermissionDecision.Allow(
                $"Allowed by rule from {allowRule.Source} (pattern: '{allowRule.ToolPattern}').",
                allowRule);

            LogDecision(agentId, toolName, allowDecision);
            return allowDecision;
        }

        // No matching rule: default to Ask
        var defaultDecision = PermissionDecision.Ask(
            $"No matching permission rule for tool '{toolName}'. Defaulting to Ask.");

        LogDecision(agentId, toolName, defaultDecision);
        return defaultDecision;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsToolAllowedAsync(
        string agentId,
        string toolName,
        CancellationToken cancellationToken)
    {
        var decision = await ResolvePermissionAsync(agentId, toolName, cancellationToken: cancellationToken);
        return decision.Behavior == PermissionBehaviorType.Allow;
    }

    private async Task<IReadOnlyList<ToolPermissionRule>> CollectRulesAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        var allRules = new List<ToolPermissionRule>();

        foreach (var provider in _ruleProviders)
        {
            var rules = await provider.GetRulesAsync(agentId, cancellationToken);
            allRules.AddRange(rules);
        }

        return allRules;
    }

    /// <summary>
    /// Finds the first rule matching the tool, operation, and <paramref name="behavior"/> in priority
    /// order, for the phase-ordered scans (Deny, then Ask, then Allow).
    /// </summary>
    /// <remarks>
    /// Authoritative-baseline rules are deliberately excluded: they are arbitrated as a set in phase 1.5
    /// by <see cref="FindFirstAuthoritativeBaseline"/>, which weighs specificity against restrictiveness.
    /// Letting them also participate here would let phase ordering pre-empt that arbitration — a
    /// catch-all baseline Deny would match in phase 1b and kill the specific baseline Allows that are
    /// supposed to outrank it. Excluding them costs nothing in the other two phases: any baseline that
    /// matches has already caused phase 1.5 to return, so phases 2 and 3 never see a matching one.
    /// </remarks>
    private ToolPermissionRule? FindFirstMatchingRule(
        IReadOnlyList<ToolPermissionRule> rules,
        string toolName,
        string? operation,
        PermissionBehaviorType behavior)
    {
        foreach (var rule in rules)
        {
            if (rule.IsAuthoritativeBaseline)
                continue;

            if (rule.Behavior != behavior)
                continue;

            if (!Matches(rule, toolName, operation))
                continue;

            return rule;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="rule"/> applies to this tool name and operation. A rule that names an
    /// operation pattern applies only when an operation was supplied and matches it.
    /// </summary>
    private bool Matches(ToolPermissionRule rule, string toolName, string? operation)
    {
        if (!_patternMatcher.IsMatch(rule.ToolPattern, toolName))
            return false;

        if (rule.OperationPattern is null)
            return true;

        return operation is not null && _patternMatcher.IsMatch(rule.OperationPattern, operation);
    }

    /// <summary>
    /// Selects the governing rule flagged <see cref="ToolPermissionRule.IsAuthoritativeBaseline"/>
    /// among all that match the tool name and operation. Returns null when none matches (the
    /// overwhelmingly common case).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arbitration is <b>specificity first, restrictiveness second</b>:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   The baseline whose pattern selects most narrowly wins outright (see
    ///   <see cref="IPatternMatcher.Specificity"/>). This is what lets a provider express "deny anything
    ///   I did not name" as a catch-all baseline alongside per-name grants: the grants are written for
    ///   one exact name and therefore outrank the catch-all, while a name no grant covers falls through
    ///   to it. Without specificity ordering the catch-all Deny — being the most restrictive rule in the
    ///   set — would swallow every grant.
    ///   </description></item>
    ///   <item><description>
    ///   Among equally specific baselines the <b>most restrictive</b> behavior wins — Deny &gt; Ask &gt;
    ///   Allow — so two plugins declaring the same tool name with opposite autonomy levels resolve to the
    ///   stricter one and never to whichever happened to load first.
    ///   </description></item>
    ///   <item><description>
    ///   Ties fall back to the lowest <see cref="ToolPermissionRule.Priority"/>.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Specificity ranks above restrictiveness rather than below it because a broad pattern is a
    /// <em>default</em> and a narrow one is a <em>decision</em> about a specific name: an operator who
    /// names a tool explicitly has said something more deliberate than whoever wrote the fallback, in
    /// either direction. Restrictiveness still governs the case the operator cannot disambiguate —
    /// two equally specific claims on the same name.
    /// </para>
    /// </remarks>
    private ToolPermissionRule? FindFirstAuthoritativeBaseline(
        IReadOnlyList<ToolPermissionRule> rules,
        string toolName,
        string? operation)
    {
        ToolPermissionRule? best = null;

        foreach (var rule in rules)
        {
            if (!rule.IsAuthoritativeBaseline)
                continue;

            if (!Matches(rule, toolName, operation))
                continue;

            if (best is null || Outranks(rule, best))
                best = rule;
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> should govern instead of <paramref name="incumbent"/>:
    /// a strictly more specific pattern wins; failing that a strictly more restrictive behavior wins
    /// (Deny &gt; Ask &gt; Allow); failing that the lower (earlier) priority wins.
    /// </summary>
    private bool Outranks(ToolPermissionRule candidate, ToolPermissionRule incumbent)
    {
        var candidateSpecificity = _patternMatcher.Specificity(candidate.ToolPattern);
        var incumbentSpecificity = _patternMatcher.Specificity(incumbent.ToolPattern);

        if (candidateSpecificity != incumbentSpecificity)
            return candidateSpecificity > incumbentSpecificity;

        var candidateRank = RestrictivenessRank(candidate.Behavior);
        var incumbentRank = RestrictivenessRank(incumbent.Behavior);

        if (candidateRank != incumbentRank)
            return candidateRank < incumbentRank;

        return candidate.Priority < incumbent.Priority;
    }

    private static int RestrictivenessRank(PermissionBehaviorType behavior) => behavior switch
    {
        PermissionBehaviorType.Deny => 0,
        PermissionBehaviorType.Ask => 1,
        _ => 2
    };

    private void LogDecision(string agentId, string toolName, PermissionDecision decision)
    {
        _logger.LogDebug(
            "Permission resolved for agent {AgentId}, tool {ToolName}: {Decision} - {Reason}",
            agentId, toolName, decision.Behavior, decision.Reason);

        Activity.Current?.SetTag(PermissionConventions.ToolName, toolName);
        Activity.Current?.SetTag(PermissionConventions.Decision, decision.Behavior.ToString());

        if (decision.Source is not null)
            Activity.Current?.SetTag(PermissionConventions.RuleSource, decision.Source.ToString());
    }
}
