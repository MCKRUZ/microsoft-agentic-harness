using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;

namespace Application.Core.Escalation.Strategies;

/// <summary>
/// Unanimous approval required. A single denial resolves the escalation as denied immediately.
/// </summary>
public sealed class AllOfApprovalStrategy : IApprovalStrategy
{
    /// <inheritdoc />
    public ApprovalStrategyType StrategyType => ApprovalStrategyType.AllOf;

    /// <inheritdoc />
    public ApprovalEvaluation EvaluateDecision(
        EscalationRequest request,
        IReadOnlyList<ApproverDecision> decisions)
    {
        if (request.Approvers.Count == 0)
        {
            // Fail closed: an empty roster is a misconfigured gate. Treating "no approvers
            // pending" as vacuously unanimous would auto-approve on the first decision (or
            // even with none). Governance code must never approve a gate that nobody owns.
            return new ApprovalEvaluation
            {
                IsResolved = true,
                Verdict = ApproverVerdict.Deny,
                PendingApprovers = []
            };
        }

        var scoped = ApproverRoster.Scope(request, decisions);
        var tally = new VerdictTally(scoped.Decisions);

        // A single denial from a listed approver resolves immediately as denied, with or
        // without other approvers still pending: no pending vote can undo a hard no.
        if (tally.DenyCount > 0)
        {
            return new ApprovalEvaluation
            {
                IsResolved = true,
                Verdict = ApproverVerdict.Deny,
                PendingApprovers = scoped.Pending
            };
        }

        // A revise, unlike a denial, must NOT short-circuit while approvers are still pending —
        // a pending approver may yet deny, and resolving Revise here would soften that possible
        // hard no into "try again" before it had the chance to land.
        if (scoped.Pending.Count > 0)
        {
            return new ApprovalEvaluation
            {
                IsResolved = false,
                Verdict = ApproverVerdict.Deny,
                PendingApprovers = scoped.Pending
            };
        }

        // Deferring to VerdictTally.Resolve() rather than re-deriving revise-beats-approve here:
        // DenyCount is 0 (checked above) and nobody is pending (checked above), so the roster is
        // non-empty and fully responded with no denial -- ApproveCount is therefore guaranteed
        // positive whenever ReviseCount is 0, and Resolve() is never null. One precedence rule,
        // expressed once.
        return new ApprovalEvaluation
        {
            IsResolved = true,
            Verdict = tally.Resolve()!.Value,
            PendingApprovers = scoped.Pending
        };
    }
}
