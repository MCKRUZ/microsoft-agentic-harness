using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;

namespace Infrastructure.AI.Helpers;

/// <summary>
/// The unbounded, single-reader channel behind every in-memory dispatch queue, reporting how many ids are
/// waiting (<see cref="DispatchMetrics.QueueDepth"/>) and how long each waited before its dispatcher took it
/// (<see cref="DispatchMetrics.QueueWait"/>).
/// </summary>
/// <remarks>
/// <para>
/// The bundle-run, workflow-run and change-proposal queues are the same FIFO hand-off from request handlers
/// to one background dispatcher. They share this type so all three report the same two measurements the
/// same way, tagged by <see cref="DispatchConventions.Queue"/>, instead of each keeping a hand copy.
/// </para>
/// <para>
/// Unbounded, so <see cref="EnqueueAsync"/> never blocks a request waiting for capacity, and single-reader,
/// because each queue has exactly one dispatcher. Many request handlers write at once, so it is not
/// single-writer.
/// </para>
/// <para>
/// The depth goes up before the write, not after. The other order lets the dispatcher take an id and count
/// it down before it was counted up, so a scrape between the two would read a negative depth.
/// </para>
/// </remarks>
public sealed class InstrumentedDispatchChannel
{
    private readonly Channel<Entry> _channel = Channel.CreateUnbounded<Entry>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    private readonly KeyValuePair<string, object?> _queueTag;

    /// <summary>Initializes a channel whose measurements carry <paramref name="queueName"/>.</summary>
    /// <param name="queueName">The <see cref="DispatchConventions.Queue"/> value, for example <see cref="DispatchConventions.WorkflowRunQueue"/>.</param>
    public InstrumentedDispatchChannel(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        _queueTag = new KeyValuePair<string, object?>(DispatchConventions.Queue, queueName);
    }

    /// <summary>Adds <paramref name="id"/> to the back of the queue.</summary>
    /// <param name="id">The id of the accepted work.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask EnqueueAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        DispatchMetrics.QueueDepth.Add(1, _queueTag);
        try
        {
            await _channel.Writer.WriteAsync(new Entry(id, Stopwatch.GetTimestamp()), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DispatchMetrics.QueueDepth.Add(-1, _queueTag);
            throw;
        }
    }

    /// <summary>
    /// Yields ids in the order they were enqueued, as the dispatcher asks for them, until
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token, normally the host's stopping token.</param>
    public async IAsyncEnumerable<string> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            DispatchMetrics.QueueDepth.Add(-1, _queueTag);
            DispatchMetrics.QueueWait.Record(Stopwatch.GetElapsedTime(entry.EnqueuedAt).TotalMilliseconds, _queueTag);
            yield return entry.Id;
        }
    }

    private readonly record struct Entry(string Id, long EnqueuedAt);
}
