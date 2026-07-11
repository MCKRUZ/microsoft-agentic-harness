namespace Application.AI.Common.Interfaces.Bundles;

/// <summary>
/// Queue of run job ids awaiting background dispatch. The <c>RunBundleCommand</c> handler enqueues a job id
/// after creating its <c>BundleRunRecord</c>; a background worker (<c>BundleRunBackgroundService</c>) drains
/// the queue and executes each run, so the command handler returns a job id immediately instead of blocking
/// the request thread on a multi-turn agent conversation.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the <c>IChangeProposalDispatchQueue</c> contract exactly (<c>Enqueue</c> + <c>DequeueAll</c>): the
/// default in-memory implementation backs a single-reader <c>Channel&lt;string&gt;</c>, so ids enqueued
/// before a host crash are lost — the same non-durable semantics as the in-memory run-job store they pair
/// with. A consumer needing at-least-once delivery swaps this for an outbox-backed implementation without
/// changing the worker.
/// </para>
/// <para>
/// The queue carries only the job id; the worker loads the full <c>BundleRunRecord</c> from the job store,
/// which is what lets the run be picked up on a thread detached from the request that enqueued it (the
/// record carries the capability envelope the worker must re-arm — the ambient one does not flow across the
/// thread-pool boundary).
/// </para>
/// </remarks>
public interface IBundleRunDispatchQueue
{
    /// <summary>
    /// Adds a run job id to the queue for asynchronous dispatch. Returns once the id is queued, not once the
    /// run has executed.
    /// </summary>
    /// <param name="jobId">The run job to dispatch.</param>
    /// <param name="cancellationToken">
    /// Cancellation token honored by bounded-queue implementations that may wait for capacity. Unbounded
    /// implementations complete synchronously.
    /// </param>
    ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Streams run job ids out of the queue in FIFO order, yielding each as it becomes available and
    /// completing when the queue is shut down (host stop or cancellation).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token; cancelling stops the enumeration.</param>
    /// <returns>An async stream of run job ids ready for dispatch.</returns>
    IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken);
}
