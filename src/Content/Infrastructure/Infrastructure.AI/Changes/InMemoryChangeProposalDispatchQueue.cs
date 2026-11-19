using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Telemetry.Conventions;
using Infrastructure.AI.Helpers;

namespace Infrastructure.AI.Changes;

/// <summary>
/// In-memory <see cref="IChangeProposalDispatchQueue"/> backed by an
/// <see cref="InstrumentedDispatchChannel"/>. FIFO; unbounded so <see cref="EnqueueAsync"/> never
/// blocks the caller waiting for capacity.
/// </summary>
/// <remarks>
/// <para>
/// Process-local — ids enqueued in one host instance are not visible to
/// another. Crash-loses the queue (matches the in-memory store's restart
/// semantics; <see cref="Infrastructure.AI.Changes.InMemoryChangeProposalStore"/>
/// has the same property and the startup validator forces consumers to
/// explicitly opt in to it outside Development).
/// </para>
/// <para>
/// Single-reader: the channel is configured with
/// <c>SingleReader = true</c> because only the
/// <c>ChangeProposalBackgroundService</c> reads from it. This lets the
/// runtime use a cheaper synchronization primitive than the multi-reader
/// path. If a future deployment fans out to multiple workers, drop the flag.
/// </para>
/// <para>
/// Its depth and wait are reported through <see cref="InstrumentedDispatchChannel"/>.
/// </para>
/// </remarks>
public sealed class InMemoryChangeProposalDispatchQueue : IChangeProposalDispatchQueue
{
    private readonly InstrumentedDispatchChannel _channel = new(DispatchConventions.ChangeProposalQueue);

    /// <inheritdoc />
    public ValueTask EnqueueAsync(string proposalId, CancellationToken cancellationToken)
        => _channel.EnqueueAsync(proposalId, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.DequeueAllAsync(cancellationToken);
}
