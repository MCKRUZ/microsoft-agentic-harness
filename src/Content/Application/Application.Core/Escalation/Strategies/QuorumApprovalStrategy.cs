using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;

namespace Application.Core.Escalation.Strategies;

/// <summary>
/// N-of-M threshold approval. Resolves as soon as the outcome is mathematically determined --
/// either enough approvals to meet quorum, or enough denials to make quorum impossible.
/// </summary>
public sealed class QuorumApprovalStrategy : IApprovalStrategy
{
    /// <inheritdoc />
    public ApprovalStrategyType StrategyType => ApprovalStrategyType.Quorum;

    /// <inheritdoc />
    public ApprovalEvaluation EvaluateDecision(
        EscalationRequest request,
        IReadOnlyList<ApproverDecision> decisions)
    {
        var approverSet = request.Approvers.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only count decisions from identities that are actually listed as approvers.
        // Votes from non-listed identities must not satisfy quorum nor corrupt the
        // remaining-vote math (mirrors the membership filter used for `pending`).
        var deduplicated = DeduplicateByApprover(decisions)
            .Where(d => approverSet.Contains(d.ApproverName))
            .ToArray();
        var respondedNames = deduplicated.Select(d => d.ApproverName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = request.Approvers.Where(a => !respondedNames.Contains(a)).ToArray();

        var quorumThreshold = request.QuorumThreshold;
        if (quorumThreshold <= 0)
        {
            // Fail closed: a non-positive quorum threshold is a misconfigured gate
            // (QuorumThreshold defaults to 0 and is not validated upstream). Governance
            // code must never auto-approve on a default-valued field -- resolve as denied.
            return new ApprovalEvaluation
            {
                IsResolved = true,
                IsApproved = false,
                PendingApprovers = pending
            };
        }

        var approvedCount = deduplicated.Count(d => d.Approved);
        var deniedCount = deduplicated.Count(d => !d.Approved);
        var totalApprovers = request.Approvers.Count;

        if (approvedCount >= quorumThreshold)
        {
            return new ApprovalEvaluation
            {
                IsResolved = true,
                IsApproved = true,
                PendingApprovers = pending
            };
        }

        var remainingVotes = totalApprovers - approvedCount - deniedCount;
        if (approvedCount + remainingVotes < quorumThreshold)
        {
            return new ApprovalEvaluation
            {
                IsResolved = true,
                IsApproved = false,
                PendingApprovers = pending
            };
        }

        return new ApprovalEvaluation
        {
            IsResolved = false,
            IsApproved = false,
            PendingApprovers = pending
        };
    }

    private static IReadOnlyList<ApproverDecision> DeduplicateByApprover(IReadOnlyList<ApproverDecision> decisions) =>
        decisions
            .GroupBy(d => d.ApproverName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MinBy(d => d.RespondedAt)!)
            .ToArray();
}
