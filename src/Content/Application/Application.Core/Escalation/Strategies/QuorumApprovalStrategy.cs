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
        // Only count decisions from identities that are actually listed as approvers.
        // Votes from non-listed identities must not satisfy quorum nor corrupt the
        // remaining-vote math (shared with AnyOf/AllOf via ApproverRoster.Scope).
        var scoped = ApproverRoster.Scope(request, decisions);
        var deduplicated = scoped.Decisions;
        var pending = scoped.Pending;

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
}
