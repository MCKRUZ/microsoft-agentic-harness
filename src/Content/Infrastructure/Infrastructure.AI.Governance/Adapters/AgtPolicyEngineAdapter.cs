using System.Diagnostics;
using AgentGovernance.Policy;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>Wraps the AGT <see cref="PolicyEngine"/> behind the harness-owned <see cref="IGovernancePolicyEngine"/>.</summary>
internal sealed class AgtPolicyEngineAdapter : IGovernancePolicyEngine
{
    private readonly PolicyEngine _engine;

    public AgtPolicyEngineAdapter(PolicyEngine engine) => _engine = engine;

    public bool HasPolicies => _engine.ListPolicies().Count > 0;

    public GovernanceDecision EvaluateToolCall(
        string agentId,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var context = new Dictionary<string, object> { ["tool"] = toolName };
        if (arguments is not null)
        {
            // Null-valued arguments are skipped rather than substituted. The AGT rule context holds
            // non-null values, and mapping a null to a placeholder would let a rule match a value the
            // caller never supplied. An absent key simply fails to match, which is the honest reading.
            foreach (var kvp in arguments)
                if (kvp.Value is not null)
                    context[kvp.Key] = kvp.Value;
        }

        var sw = Stopwatch.StartNew();
        var decision = _engine.Evaluate(agentId, context);
        sw.Stop();
        var ms = sw.Elapsed.TotalMilliseconds;

        var action = ParseAction(decision.Action);
        var tags = new KeyValuePair<string, object?>[]
        {
            new(GovernanceConventions.Action, decision.Action ?? "allow"),
            new(GovernanceConventions.ToolName, toolName)
        };

        GovernanceMetrics.Decisions.Add(1, tags);
        GovernanceMetrics.EvaluationDuration.Record(ms);

        if (!decision.Allowed)
        {
            GovernanceMetrics.Violations.Add(1,
                new KeyValuePair<string, object?>(GovernanceConventions.PolicyName, decision.MatchedRule ?? "unknown"),
                new KeyValuePair<string, object?>(GovernanceConventions.RuleName, decision.MatchedRule ?? "unknown"));

            if (decision.RateLimited)
                GovernanceMetrics.RateLimitHits.Add(1,
                    new KeyValuePair<string, object?>(GovernanceConventions.ToolName, toolName));

            return new GovernanceDecision(
                false,
                action,
                decision.Reason ?? "Denied by policy",
                decision.MatchedRule,
                decision.MatchedRule,
                ms,
                decision.RateLimited,
                decision.Approvers?.AsReadOnly());
        }

        return GovernanceDecision.Allowed(ms);
    }

    // Reads once via PolicyYamlGuard (which also validates) and hands the same content to LoadYaml,
    // rather than reading the file a second time through LoadYamlFile — see PolicyYamlGuard's remarks
    // for why this specific mistake needs a guard at all, and why this is only one of two required
    // call sites (the other is DependencyInjection.AddGovernanceDependencies, for the startup path).
    public void LoadPolicyFile(string yamlPath) =>
        _engine.LoadYaml(PolicyYamlGuard.ReadAndValidate(yamlPath));

    private static GovernancePolicyAction ParseAction(string? action) => action?.ToLowerInvariant() switch
    {
        "allow" => GovernancePolicyAction.Allow,
        "deny" => GovernancePolicyAction.Deny,
        "warn" => GovernancePolicyAction.Warn,
        "require_approval" or "requireapproval" => GovernancePolicyAction.RequireApproval,
        "log" => GovernancePolicyAction.Log,
        "rate_limit" or "ratelimit" => GovernancePolicyAction.RateLimit,
        _ => GovernancePolicyAction.Deny
    };
}
