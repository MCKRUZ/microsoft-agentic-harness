using Domain.AI.Runs;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Withdraws the approvals a run was waiting on when that run is given up on.
/// </summary>
/// <remarks>
/// <para>
/// Shared because there are two ways a parked run ends without its gate being answered — a caller
/// cancels it, or the host's parked-run ceiling gives up on it — and both must release the same thing.
/// An approval that outlives its run sits in a person's queue with nothing left to decide, and
/// answering it is not harmless: a workflow's plan state outlives any one run, so a verdict given for a
/// dead run is reconciled by the next one.
/// </para>
/// <para>
/// One implementation rather than two, because the part that is easy to get wrong is not the loop —
/// it is knowing that a "no pending escalation with that id" failure is the ordinary case rather than a
/// fault. A second copy that logged it as an error would page an operator every time a gate timed out
/// microseconds before someone cancelled its run.
/// </para>
/// </remarks>
public static class ApprovalWithdrawal
{
    /// <summary>
    /// Withdraws every approval <paramref name="abandoned"/> was parked on, returning how many were
    /// actually withdrawn.
    /// </summary>
    /// <param name="escalations">The escalation lifecycle service.</param>
    /// <param name="logger">Records approvals genuinely left pending; never fails the caller.</param>
    /// <param name="abandoned">The run as it stood while parked, carrying what it was waiting on.</param>
    /// <param name="reason">Caller-safe reason recorded on the escalation's audit trail.</param>
    /// <param name="withdrawnBy">
    /// Who to attribute the withdrawal to. Should be an approver-shaped identity where one is
    /// available, since it lands beside approver names in the escalation audit.
    /// </param>
    /// <param name="cancellationToken">Cancels the withdrawal.</param>
    /// <returns>
    /// How many approvals were withdrawn — not how many were attempted. The difference is the ones
    /// that had already been decided, which is information the caller can legitimately report.
    /// </returns>
    /// <remarks>
    /// A failure to withdraw one never fails the caller. The run is already over by this point, and
    /// reporting failure would invite a retry of an operation with nothing left to do — while the thing
    /// that actually went wrong is not something the caller can fix.
    /// </remarks>
    public static async Task<int> WithdrawAsync(
        IEscalationService escalations,
        ILogger logger,
        RunRecord abandoned,
        string reason,
        string withdrawnBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(escalations);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(abandoned);

        var withdrawn = 0;

        foreach (var escalationId in abandoned.AwaitingEscalationIds)
        {
            try
            {
                await escalations
                    .CancelEscalationAsync(escalationId, reason, withdrawnBy, cancellationToken)
                    .ConfigureAwait(false);

                withdrawn++;
            }
            catch (InvalidOperationException)
            {
                // Documented contract of the escalation service: no pending escalation with that id.
                // It was decided or timed out between the run parking and this withdrawal, so there is
                // nothing in anyone's queue and nothing to report.
                logger.LogDebug(
                    "Approval {EscalationId} for abandoned run {JobId} was already resolved.",
                    escalationId, abandoned.JobId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Run {JobId} was given up on but its approval {EscalationId} could not be withdrawn; "
                    + "it remains pending in its approvers' queues with nothing left to decide.",
                    abandoned.JobId, escalationId);
            }
        }

        return withdrawn;
    }
}
