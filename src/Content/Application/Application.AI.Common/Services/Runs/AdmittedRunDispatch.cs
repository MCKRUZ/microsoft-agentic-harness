using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;

namespace Application.AI.Common.Services.Runs;

/// <summary>
/// What happens to a run after the run store has admitted it: hand it to the dispatcher, and if that
/// fails, record it as failed so it releases what it holds. Shared by every path that starts a run
/// (#731): the workflow and evaluation command handlers and the schedule dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is one place.</strong> Past admission the record is committed, and a record that is
/// committed but never queued is never claimed, never finishes, and (only terminal runs being reclaimed)
/// never goes away: it holds one of its owner's concurrency slots, and for a workflow it pins the target
/// at "already running", for the life of the process. Each of the three callers used to carry its own
/// copy of the two rules that prevent that. Admission itself is not here because it already is one
/// call, <see cref="IRunJobStore.TryCreate"/>; what differs between callers is how a refusal is
/// explained, which stays with them.
/// </para>
/// <para>
/// <strong>The queue is never given a caller's token.</strong> A client that hangs up mid-request, or a
/// tick that is cancelled at shutdown, is ordinary; stranding an admitted run must not be reachable that
/// way. The caller was already told, or is about to be told, that the run was accepted.
/// </para>
/// </remarks>
public static class AdmittedRunDispatch
{
    /// <summary>The error recorded on a run that was admitted but could not be queued.</summary>
    public const string NotQueuedError = "The run was accepted but could not be queued for execution.";

    /// <summary>
    /// Queues an admitted run, or records it as failed when it cannot be queued.
    /// </summary>
    /// <param name="queue">The dispatcher's queue.</param>
    /// <param name="store">The run store that admitted <paramref name="record"/>.</param>
    /// <param name="record">The admitted run.</param>
    /// <param name="failedAt">
    /// The completion time to record if queuing fails. A function so each caller keeps its own clock: the
    /// command handlers stamp the moment of failure, the schedule dispatcher stamps the tick that fired.
    /// </param>
    /// <returns>
    /// Null when the run was queued; otherwise the exception, after the run has been recorded as failed,
    /// so the caller can log it in its own terms. Never throws for a queue failure.
    /// </returns>
    public static async Task<Exception?> QueueOrAbandonAsync(
        IRunDispatchQueue queue, IRunJobStore store, RunRecord record, Func<DateTimeOffset> failedAt)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(failedAt);

        try
        {
            await queue.EnqueueAsync(record.JobId, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            // Unreachable with an unbounded in-process channel. Guarded because the queue is the seam for a
            // durable one, where a write can fail, and the cost of an unguarded failure is a permanently
            // held slot rather than a lost run.
            Abandon(store, record, NotQueuedError, failedAt());
            return ex;
        }
    }

    /// <summary>
    /// Records an admitted run as failed when it could not be set up to execute.
    /// </summary>
    /// <remarks>
    /// Failed rather than deleted: the record is what the sweeper reclaims, and it only reclaims terminal
    /// runs, so making this one terminal is what releases it and anything keyed by the same job id.
    /// </remarks>
    /// <param name="store">The run store that admitted <paramref name="record"/>.</param>
    /// <param name="record">The admitted run.</param>
    /// <param name="error">The caller-safe reason recorded on the run.</param>
    /// <param name="failedAt">The completion time recorded on the run.</param>
    public static void Abandon(IRunJobStore store, RunRecord record, string error, DateTimeOffset failedAt)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(record);

        store.Update(record with
        {
            Status = RunStatus.Failed,
            Error = error,
            CompletedAt = failedAt
        });
    }
}
