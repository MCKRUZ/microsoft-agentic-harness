using Domain.AI.Changes;

namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// Drives a <see cref="ChangeProposal"/> through its gate pipeline as far as it
/// can go without external intervention. Picks up at the proposal's current
/// status and pushes through validation gates, the merge gate, or both — pausing
/// at <c>AwaitingApproval</c> for human approval and at any <c>GateAction.Defer</c>
/// for an upstream retry.
/// </summary>
/// <remarks>
/// <para>
/// Re-entrant safe: the orchestrator can be invoked repeatedly against the same
/// proposal id, and idempotent on terminal status (returns the cached terminal
/// proposal verbatim). Typical callers:
/// </para>
/// <list type="bullet">
///   <item><description>Right after <c>SubmitChangeProposalCommand</c> to push a Draft as far as the gates allow.</description></item>
///   <item><description>Right after <c>ApproveChangeProposalCommand</c> to advance Approved → Merged.</description></item>
///   <item><description>From a periodic background sweep to retry Deferred proposals.</description></item>
/// </list>
/// </remarks>
public interface IChangeProposalOrchestrator
{
    /// <summary>
    /// Process the proposal forward through its gate pipeline. Returns the
    /// proposal in its post-processing state — possibly Merged, Rejected,
    /// AwaitingApproval, or Validating (deferred).
    /// </summary>
    /// <param name="proposalId">The proposal id to advance.</param>
    /// <param name="mode">The orchestrator mode for this run. Typically pulled from <c>ChangesConfig.DefaultMode</c>.</param>
    /// <param name="cancellationToken">Cancellation token honored between gate evaluations.</param>
    /// <returns>The proposal after processing. Null when the proposal id is unknown.</returns>
    Task<ChangeProposal?> ProcessAsync(
        string proposalId,
        OrchestratorMode mode,
        CancellationToken cancellationToken);
}
