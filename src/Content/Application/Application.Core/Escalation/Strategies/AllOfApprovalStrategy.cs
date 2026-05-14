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
        var deduplicated = DeduplicateByApprover(decisions);
        var respondedNames = deduplicated.Select(d => d.ApproverName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = request.Approvers.Where(a => !respondedNames.Contains(a)).ToArray();

        if (deduplicated.Any(d => !d.Approved))
        {
            return new ApprovalEvaluation
            {
                IsResolved = true,
                IsApproved = false,
                PendingApprovers = pending
            };
        }

        var allResponded = pending.Length == 0;
        return new ApprovalEvaluation
        {
            IsResolved = allResponded,
            IsApproved = allResponded,
            PendingApprovers = pending
        };
    }

    private static IReadOnlyList<ApproverDecision> DeduplicateByApprover(IReadOnlyList<ApproverDecision> decisions) =>
        decisions
            .GroupBy(d => d.ApproverName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MinBy(d => d.RespondedAt)!)
            .ToArray();
}
