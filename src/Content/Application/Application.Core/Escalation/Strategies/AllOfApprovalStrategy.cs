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

        return new ApprovalEvaluation
        {
            IsResolved = true,
            Verdict = tally.ReviseCount > 0 ? ApproverVerdict.Revise : ApproverVerdict.Approve,
            PendingApprovers = scoped.Pending
        };
    }
}
