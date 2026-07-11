using System.Threading.Channels;
using Application.AI.Common.Interfaces.Bundles;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// In-memory <see cref="IBundleRunDispatchQueue"/> backed by a single-reader <see cref="Channel{T}"/>.
/// FIFO; unbounded so <see cref="EnqueueAsync"/> never blocks the caller waiting for capacity. Mirrors
/// <see cref="Infrastructure.AI.Changes.InMemoryChangeProposalDispatchQueue"/>.
/// </summary>
/// <remarks>
/// Process-local and crash-loses the queue — the same non-durable semantics as the in-memory run-job store
/// it pairs with, and consistent with bundle runs not being persisted. Single-reader because only
/// <c>BundleRunBackgroundService</c> reads it; multiple producers (concurrent run requests) are expected, so
/// <c>SingleWriter</c> is not set.
/// </remarks>
public sealed class InMemoryBundleRunDispatchQueue : IBundleRunDispatchQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    /// <inheritdoc />
    public ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        return _channel.Writer.WriteAsync(jobId, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
