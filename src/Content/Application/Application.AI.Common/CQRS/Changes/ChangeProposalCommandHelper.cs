using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;

namespace Application.AI.Common.CQRS.Changes;

/// <summary>
/// Shared <c>load → status guard → transition + save → optional post-save</c>
/// skeleton for the <c>Approve</c>, <c>Reject</c>, and <c>Cancel</c> command
/// handlers. Each handler injects its handler-specific guard predicate,
/// <see cref="GateDecision"/> factory, target status, and an optional post-save
/// hook (Approve uses it to drive the orchestrator inline so the merge phase
/// runs before the command returns).
/// </summary>
/// <remarks>
/// Lives in <c>Application.AI.Common.CQRS.Changes</c> rather than inside any
/// single handler's folder because all three handlers consume it. Kept as an
/// internal static helper instead of an abstract base because the handlers have
/// divergent dependencies (Approve takes an orchestrator + config; the others
/// take only the store + clock) and base-class ceremony would obscure that.
/// </remarks>
internal static class ChangeProposalCommandHelper
{
    /// <summary>
    /// Run the shared decision pipeline against an existing proposal.
    /// </summary>
    /// <param name="store">The proposal store to load + save from.</param>
    /// <param name="proposalId">The id of the proposal to act on.</param>
    /// <param name="statusGuard">
    /// Returns a non-null <see cref="Result{T}"/> to short-circuit the pipeline
    /// (typically <c>Fail</c> when the current status doesn't permit the
    /// decision), or <c>null</c> to proceed. The proposal is passed in so the
    /// guard can include its actual current status in the failure message.
    /// </param>
    /// <param name="decisionFactory">Builds the <see cref="GateDecision"/> to record on the transition.</param>
    /// <param name="targetStatus">The status to transition the proposal into.</param>
    /// <param name="postSave">
    /// Optional follow-up after the transition is persisted. When non-null its
    /// result replaces the default <c>Success(transitioned)</c> return — used by
    /// Approve to drive the orchestrator forward and return the post-merge
    /// proposal snapshot.
    /// </param>
    /// <param name="cancellationToken">Cancellation token forwarded to the store + post-save hook.</param>
    public static async Task<Result<ChangeProposal>> ApplyDecisionAsync(
        IChangeProposalStore store,
        string proposalId,
        Func<ChangeProposal, Result<ChangeProposal>?> statusGuard,
        Func<GateDecision> decisionFactory,
        ChangeProposalStatus targetStatus,
        Func<ChangeProposal, CancellationToken, Task<Result<ChangeProposal>>>? postSave,
        CancellationToken cancellationToken)
    {
        var proposal = await store.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
        {
            return Result<ChangeProposal>.NotFound($"ChangeProposal '{proposalId}' not found.");
        }

        var guard = statusGuard(proposal);
        if (guard is not null)
        {
            return guard;
        }

        var decision = decisionFactory();
        var transitioned = proposal.TransitionTo(targetStatus, decision);
        await store.SaveAsync(transitioned, cancellationToken).ConfigureAwait(false);

        if (postSave is not null)
        {
            return await postSave(transitioned, cancellationToken).ConfigureAwait(false);
        }
        return Result<ChangeProposal>.Success(transitioned);
    }
}
