using Domain.AI.Escalation;

namespace Application.Core.Tests.Escalation.Strategies;

/// <summary>
/// Shared decision builders for the three approval-strategy test suites (AnyOf/AllOf/Quorum),
/// which otherwise each hand-rolled byte-identical <c>Approve</c>/<c>Deny</c>/<c>Revise</c>
/// helpers. Consumed via <c>using static</c>.
/// </summary>
internal static class ApproverDecisionFixtures
{
    public static ApproverDecision Approve(string name) => new()
    {
        ApproverName = name,
        Verdict = ApproverVerdict.Approve,
        RespondedAt = DateTimeOffset.UtcNow
    };

    public static ApproverDecision Deny(string name) => new()
    {
        ApproverName = name,
        Verdict = ApproverVerdict.Deny,
        Reason = "Denied",
        RespondedAt = DateTimeOffset.UtcNow
    };

    public static ApproverDecision Revise(string name) => new()
    {
        ApproverName = name,
        Verdict = ApproverVerdict.Revise,
        Instructions = "Use the other path",
        RespondedAt = DateTimeOffset.UtcNow
    };
}
