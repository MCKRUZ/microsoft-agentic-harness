using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;

namespace Application.Core.Escalation.Strategies;

/// <summary>
/// First response wins -- any single approval or denial resolves the escalation immediately.
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
                IsApproved = false,
                PendingApprovers = scoped.Pending
            };
        }

        var firstDecision = scoped.Decisions.MinBy(d => d.RespondedAt)!;
        return new ApprovalEvaluation
        {
            IsResolved = true,
            IsApproved = firstDecision.Approved,
            PendingApprovers = scoped.Pending
        };
    }
}
