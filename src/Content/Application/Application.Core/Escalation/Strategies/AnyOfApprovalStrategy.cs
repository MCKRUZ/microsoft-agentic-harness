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
        var deduplicated = DeduplicateByApprover(decisions);
        var respondedNames = deduplicated.Select(d => d.ApproverName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = request.Approvers.Where(a => !respondedNames.Contains(a)).ToArray();

        if (deduplicated.Count == 0)
        {
            return new ApprovalEvaluation
            {
                IsResolved = false,
                IsApproved = false,
                PendingApprovers = pending
            };
        }

        var firstDecision = deduplicated.MinBy(d => d.RespondedAt)!;
        return new ApprovalEvaluation
        {
            IsResolved = true,
            IsApproved = firstDecision.Approved,
            PendingApprovers = pending
        };
    }

    private static IReadOnlyList<ApproverDecision> DeduplicateByApprover(IReadOnlyList<ApproverDecision> decisions) =>
        decisions
            .GroupBy(d => d.ApproverName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MinBy(d => d.RespondedAt)!)
            .ToArray();
}
