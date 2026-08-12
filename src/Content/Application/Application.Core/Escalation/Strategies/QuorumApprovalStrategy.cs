using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;

namespace Application.Core.Escalation.Strategies;

/// <summary>
/// N-of-M threshold approval. Resolves as soon as the outcome is mathematically determined --
/// either enough approvals to meet quorum, or enough non-approvals to make quorum impossible.
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
                Verdict = ApproverVerdict.Deny,
                PendingApprovers = pending
            };
        }

        var tally = new VerdictTally(scoped.Decisions);
        var totalApprovers = request.Approvers.Count;

        // Meeting the approval threshold wins even with denies or revises also present. This is
        // not a precedence violation -- deny > revise > approve orders competing verdicts for an
        // UNDETERMINED outcome, and here the threshold is already met. Consistency demands it:
        // today a single deny cannot block a met quorum, so a single revise must not gain a veto
        // power a denier does not have either.
        if (tally.ApproveCount >= quorumThreshold)
        {
            return new ApprovalEvaluation
            {
                IsResolved = true,
                Verdict = ApproverVerdict.Approve,
                PendingApprovers = pending
            };
        }

        // A revise is a cast vote, not a pending one -- it can never turn into an approval within
        // this escalation, so it counts against the remaining pool exactly like a deny does.
        var remainingVotes = totalApprovers - tally.Total;
        if (tally.ApproveCount + remainingVotes < quorumThreshold)
        {
            // Quorum has become mathematically impossible. Deferring to VerdictTally.Resolve()
            // rather than re-deriving deny-beats-revise here: QuorumThreshold is invariant-bounded
            // to at most totalApprovers, so reaching this branch guarantees at least one non-approve
            // response was cast (otherwise "impossible" could never trigger) -- Resolve() is never
            // null here, and always agrees with what a hand-written comparison would say. One
            // precedence rule, expressed once.
            return new ApprovalEvaluation
            {
                IsResolved = true,
                Verdict = tally.Resolve()!.Value,
                PendingApprovers = pending
            };
        }

        return new ApprovalEvaluation
        {
            IsResolved = false,
            Verdict = ApproverVerdict.Deny,
            PendingApprovers = pending
        };
    }
}
