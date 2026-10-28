using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Runs;

/// <summary>
/// Stops one run and releases what it was holding. Shared by every path that cancels a run: the workflow and evaluation
/// cancel commands, and erasure, which stops the person's runs before removing their records (#992).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is one place.</strong> Cancelling is three steps that each matter: the store's atomic transition, the
/// terminal progress event that ends a watcher's stream, and withdrawing the approvals a parked run waits on. A copy that
/// dropped the event would hold every watcher's connection open; one that dropped the withdrawal would leave a request in
/// an approver's queue whose verdict the workflow's next run reconciles against. Each caller used to carry its own copy.
/// </para>
/// <para>
/// <strong>The caller decides whose run this is.</strong> <see cref="IRunJobStore.TryCancel"/> takes no owner, so this
/// cancels whatever run it is handed: the cancel commands pass a run they read through the caller's scoped
/// <see cref="IRunJobStore.Get"/>, and erasure a run it listed for the person by owner.
/// </para>
/// <para>
/// <strong>The run is stopped before its approvals are withdrawn, deliberately.</strong> Withdrawing first produces a
/// resolved escalation while the run is still parked on it, which is exactly what the resume check looks for, so the
/// cancellation itself would put the run back to work.
/// </para>
/// <para>
/// <strong>A run already executing is signalled, not rewritten, and only when the caller passes a registry.</strong> Its
/// terminal state is owned by the dispatch in flight. A workflow is signalled through
/// <see cref="IPlanRunCancellationRegistry"/>, which is keyed by plan, so the signal stops every run of that plan in this
/// process. That reaches only the run's owner's work: a workflow run is admitted only against a plan its caller can
/// write, which is a plan that caller owns, and admission allows one live run per workflow. The workflow cancel command
/// and erasure pass one; the evaluation cancel command does not, since an evaluation has no such signal and runs to its
/// end.
/// </para>
/// <para>
/// <strong>Once the transition is made, the rest always runs.</strong> The run is terminal from that moment, so nothing
/// will come back for its approvals: the parked-run ceiling looks only at parked runs. A progress broker that throws is
/// therefore logged, not allowed to skip the withdrawal.
/// </para>
/// </remarks>
public static class RunCancellation
{
    /// <summary>Cancels <paramref name="run"/> if it has not started or is parked; otherwise signals it if it can be.</summary>
    /// <param name="store">The run store holding <paramref name="run"/>.</param>
    /// <param name="progress">Publishes the terminal event for a run no dispatch will finish.</param>
    /// <param name="escalations">Withdraws the approvals a parked run was waiting on.</param>
    /// <param name="registry">
    /// Signals an executing workflow to stop, by its plan; <see langword="null"/> to leave an executing run alone.
    /// </param>
    /// <param name="logger">Records approvals genuinely left pending; never fails the caller.</param>
    /// <param name="run">The run to stop, already authorised by the caller.</param>
    /// <param name="notice">What to record, and in whose name.</param>
    /// <returns>Whether the run stopped here, and how many approvals were withdrawn with it.</returns>
    public static async Task<RunCancellationOutcome> CancelAsync(
        IRunJobStore store,
        IRunProgressBroker progress,
        IEscalationService escalations,
        IPlanRunCancellationRegistry? registry,
        ILogger logger,
        RunRecord run,
        RunCancellationNotice notice)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(escalations);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(notice);

        var stopped = store.TryCancel(run.JobId, notice.CancelledAt);
        if (stopped is null)
        {
            // Executing, or it ended since the caller read it. Either way its status belongs to the dispatch that holds
            // it, so all that is left is to ask it to stop, where the caller can.
            if (registry is not null && run.Kind == RunKind.Workflow && Guid.TryParse(run.TargetId, out var workflowId))
                registry.TryCancel(new PlanId(workflowId));

            return new RunCancellationOutcome(StoppedImmediately: false, WithdrawnApprovals: 0);
        }

        // Published here because nothing else will: no dispatch is going to run for this job, and the terminal event is
        // what ends a watcher's stream.
        try
        {
            progress.Publish(
                stopped.JobId,
                RunProgressKind.RunFinished,
                status: nameof(RunStatus.Cancelled),
                detail: notice.Detail);
        }
        catch (Exception ex)
        {
            logger.LogError(MessageWithheldException.From(ex), "Run {JobId} was cancelled but its watchers could not be told.", stopped.JobId);
        }

        var withdrawn = await ApprovalWithdrawal.WithdrawAsync(
            escalations,
            logger,
            stopped,
            notice.WithdrawalReason,
            notice.WithdrawnBy,

            // Deliberately not the caller's token. The run is already cancelled, so a caller that went away mid-withdrawal
            // would leave the rest of its approvals pending with nothing to retry them: the parked-run ceiling only looks
            // at parked runs, and this one is terminal.
            CancellationToken.None).ConfigureAwait(false);

        return new RunCancellationOutcome(StoppedImmediately: true, WithdrawnApprovals: withdrawn);
    }
}
