using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;

namespace Application.Core.Escalation.Strategies;

/// <summary>
/// First response wins -- any single decision resolves the escalation immediately.
/// </summary>
public sealed class AnyOfApprovalStrategy : IApprovalStrategy
{
    /// <inheritdoc />
    public ApprovalStrategyType StrategyType => ApprovalStrategyType.AnyOf;

    /// <inheritdoc />
    public ApprovalEvaluation EvaluateDecision(
        EscalationRequest request,
        IReadOnlyList<ApproverDecision> decisions)
    {
        var scoped = ApproverRoster.Scope(request, decisions);

        // Only decisions from listed approvers count; a non-roster vote must never resolve.
        if (scoped.Decisions.Count == 0)
        {
            return new ApprovalEvaluation
            {
                IsResolved = false,
                Verdict = ApproverVerdict.Deny,
                PendingApprovers = scoped.Pending
            };
        }

        // Precedence over the whole scoped set, not the earliest responder by timestamp: two
        // decisions can land in the collected set before the first evaluation runs, and picking
        // by RespondedAt made a governance outcome depend on a timestamp tie or clock skew. Deny
        // beats revise beats approve, deterministically, regardless of arrival order.
        var tally = new VerdictTally(scoped.Decisions);
        // Safe: scoped.Decisions.Count > 0 here (checked above), and VerdictTally counts every
        // decision — including one with an undefined verdict, as a denial — so Resolve() can
        // only return null when nothing was tallied, which cannot happen on this branch.
        return new ApprovalEvaluation
        {
            IsResolved = true,
            Verdict = tally.Resolve()!.Value,
            PendingApprovers = scoped.Pending
        };
    }
}
