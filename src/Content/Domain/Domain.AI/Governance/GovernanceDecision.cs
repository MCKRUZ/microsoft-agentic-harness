namespace Domain.AI.Governance;

/// <summary>
/// The outcome of a governance policy evaluation against an agent action.
/// Immutable value object returned by <c>IGovernancePolicyEngine</c>.
/// </summary>
public sealed record GovernanceDecision(
    bool IsAllowed,
    GovernancePolicyAction Action,
    string Reason,
    string? MatchedRule = null,
    string? PolicyName = null,
    double EvaluationMs = 0,
    bool IsRateLimited = false,
    IReadOnlyList<string>? Approvers = null)
{
    /// <summary>Creates an allowed decision with evaluation timing.</summary>
    public static GovernanceDecision Allowed(double evaluationMs = 0) =>
        new(true, GovernancePolicyAction.Allow, "Allowed by policy", EvaluationMs: evaluationMs);

    /// <summary>Creates a denied decision with rule details.</summary>
    public static GovernanceDecision Denied(string matchedRule, string policyName, string reason, double evaluationMs = 0) =>
        new(false, GovernancePolicyAction.Deny, reason, matchedRule, policyName, evaluationMs);
}
