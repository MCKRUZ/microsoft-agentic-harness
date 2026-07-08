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
                IsApproved = false,
                PendingApprovers = []
            };
        }

        var scoped = ApproverRoster.Scope(request, decisions);

        // A single denial from a listed approver resolves immediately as denied.
        if (scoped.Decisions.Any(d => !d.Approved))
        {
            return new ApprovalEvaluation
            {
                IsResolved = true,
                IsApproved = false,
                PendingApprovers = scoped.Pending
            };
        }

        var allResponded = scoped.Pending.Count == 0;
        return new ApprovalEvaluation
        {
            IsResolved = allResponded,
            IsApproved = allResponded,
            PendingApprovers = scoped.Pending
        };
    }
}
