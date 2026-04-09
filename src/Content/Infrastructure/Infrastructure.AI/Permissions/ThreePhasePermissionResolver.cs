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
///   <item><description><strong>Phase 2 (Ask)</strong>: Find the first matching Ask rule. If bypass-immune, return Ask regardless.</description></item>
///   <item><description><strong>Phase 3 (Allow)</strong>: Find the first matching Allow rule. If no match, default to Ask.</description></item>
/// </list>
/// <para>Rules are sorted by <see cref="ToolPermissionRule.Priority"/> ascending before evaluation.</para>
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

    private ToolPermissionRule? FindFirstMatchingRule(
        IReadOnlyList<ToolPermissionRule> rules,
        string toolName,
        string? operation,
        PermissionBehaviorType behavior)
    {
        foreach (var rule in rules)
        {
            if (rule.Behavior != behavior)
                continue;

            if (!_patternMatcher.IsMatch(rule.ToolPattern, toolName))
                continue;

            if (rule.OperationPattern is not null
                && operation is not null
                && !_patternMatcher.IsMatch(rule.OperationPattern, operation))
            {
                continue;
            }

            // If rule has an operation pattern but no operation was provided, skip
            if (rule.OperationPattern is not null && operation is null)
                continue;

            return rule;
        }

        return null;
    }

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
