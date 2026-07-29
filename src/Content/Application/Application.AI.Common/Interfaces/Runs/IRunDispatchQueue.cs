namespace Application.AI.Common.Interfaces.Runs;

/// <summary>
/// Hands accepted run identifiers to the background dispatcher.
/// </summary>
/// <remarks>
/// Carries identifiers rather than records, deliberately. The store is the single source of truth for
/// a run's state, so a queued identifier cannot go stale — whereas a queued copy of the record could
/// describe a run that has since been cancelled, and the dispatcher would act on the stale copy.
/// </remarks>
public interface IRunDispatchQueue
{
    /// <summary>Queues an accepted run for execution.</summary>
    /// <param name="jobId">Identifier of the run to dispatch.</param>
    /// <param name="cancellationToken">Cancels the enqueue.</param>
    ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Yields queued run identifiers until the token is cancelled.</summary>
    /// <param name="cancellationToken">Ends the stream on host shutdown.</param>
    IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken);
}
