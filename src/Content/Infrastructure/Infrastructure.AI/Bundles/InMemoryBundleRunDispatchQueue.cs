using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Telemetry.Conventions;
using Infrastructure.AI.Helpers;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// In-memory <see cref="IBundleRunDispatchQueue"/> backed by an <see cref="InstrumentedDispatchChannel"/>.
/// FIFO; unbounded so <see cref="EnqueueAsync"/> never blocks the caller waiting for capacity. Mirrors
/// <see cref="Infrastructure.AI.Changes.InMemoryChangeProposalDispatchQueue"/>.
/// </summary>
/// <remarks>
/// <para>
/// Process-local and crash-loses the queue — the same non-durable semantics as the in-memory run-job store
/// it pairs with, and consistent with bundle runs not being persisted. Single-reader because only
/// <c>BundleRunBackgroundService</c> reads it; multiple producers (concurrent run requests) are expected, so
/// <c>SingleWriter</c> is not set.
/// </para>
/// <para>
/// Its depth and wait are reported through <see cref="InstrumentedDispatchChannel"/>.
/// </para>
/// </remarks>
public sealed class InMemoryBundleRunDispatchQueue : IBundleRunDispatchQueue
{
    private readonly InstrumentedDispatchChannel _channel = new(DispatchConventions.BundleRunQueue);

    /// <inheritdoc />
    public ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken)
        => _channel.EnqueueAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.DequeueAllAsync(cancellationToken);
}
