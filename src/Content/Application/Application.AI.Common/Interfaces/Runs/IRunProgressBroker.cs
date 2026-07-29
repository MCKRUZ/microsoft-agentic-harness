using Domain.AI.Runs;

namespace Application.AI.Common.Interfaces.Runs;

/// <summary>
/// Carries progress events from a run executing on the dispatcher to whoever is watching it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Publishing must never be able to slow a run down.</strong> A watcher is an observer, not a
/// participant: a slow reader, a stalled network, or a client that opened a stream and walked away
/// must not hold up work the caller is paying for. Publishing is therefore non-blocking, and a
/// subscriber that cannot keep up loses events rather than applying backpressure.
/// </para>
/// <para>
/// <strong>Loss is reported, never silent.</strong> Every event carries a sequence number, so a
/// watcher can see that it missed some. A feed that quietly skipped an event would be worse than one
/// that admitted it — the watcher would believe it had seen the whole run.
/// </para>
/// <para>
/// <strong>Nothing is buffered for a watcher who has not arrived.</strong> Events published while no
/// one is subscribed are dropped, because holding them would mean deciding how long to keep a
/// transcript nobody may ever read. A watcher that connects late is given the run's current state
/// first and live events after — which is bounded, and truthful about what it can and cannot show.
/// </para>
/// </remarks>
public interface IRunProgressBroker
{
    /// <summary>
    /// Offers an event to whoever is watching <paramref name="jobId"/>. Returns immediately, and does
    /// nothing at all when nobody is.
    /// </summary>
    /// <param name="jobId">The run the event belongs to.</param>
    /// <param name="kind">What happened.</param>
    /// <param name="stepId">Identifier of the step involved, for step-scoped kinds.</param>
    /// <param name="stepName">Name of the step involved, for step-scoped kinds.</param>
    /// <param name="status">Where the step or run has got to.</param>
    /// <param name="detail">Caller-safe elaboration, when there is one.</param>
    void Publish(
        string jobId,
        RunProgressKind kind,
        string? stepId = null,
        string? stepName = null,
        string? status = null,
        string? detail = null);

    /// <summary>
    /// Starts watching a run. Dispose the subscription to stop, which also releases its buffer.
    /// </summary>
    /// <param name="jobId">The run to watch.</param>
    /// <param name="ownerId">Stable identity of the caller the stream is charged to.</param>
    /// <param name="tenantId">Tenant of that caller, when the host resolves one.</param>
    /// <returns>
    /// A subscription, or <see langword="null"/> when either the host or this caller is already
    /// carrying as many watchers as it permits. Refusing is deliberate: each open stream holds a
    /// connection and a buffer, so an unbounded number of them is a way to exhaust the host by asking
    /// politely — and a purely host-wide ceiling is one any single caller can occupy, denying every
    /// other tenant.
    /// </returns>
    IRunProgressSubscription? Subscribe(string jobId, string ownerId, string? tenantId);

    /// <summary>
    /// Releases the bookkeeping held for a run that will report no further progress.
    /// </summary>
    /// <remarks>
    /// Separate from unsubscribing on purpose: a watcher leaving does not mean the run is over, and
    /// deciding that it does is what makes a watcher arriving at the same moment vanish. Called when
    /// the run's own records are reclaimed, by which point it is terminal and nothing can publish for
    /// it again. Without a caller, a host holds one entry per run it ever streamed for its whole life.
    /// </remarks>
    /// <param name="jobId">The run to forget.</param>
    void Forget(string jobId);
}

/// <summary>
/// One watcher's view of a run's progress.
/// </summary>
public interface IRunProgressSubscription : IDisposable
{
    /// <summary>Yields events until the subscription is disposed or the token is cancelled.</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    IAsyncEnumerable<RunProgressEvent> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// How many events this watcher has missed because it could not keep up.
    /// </summary>
    /// <remarks>
    /// Read alongside the events themselves so a stream can tell its client that it fell behind.
    /// Non-zero means the sequence numbers a client has seen contain gaps.
    /// </remarks>
    long DroppedCount { get; }
}
