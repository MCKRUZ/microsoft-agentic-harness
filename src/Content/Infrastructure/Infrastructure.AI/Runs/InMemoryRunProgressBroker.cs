using System.Collections.Concurrent;
using System.Threading.Channels;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Process-local <see cref="IRunProgressBroker"/>. One bounded buffer per watcher.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Per watcher, not per run.</strong> Two clients watching the same run read at their own
/// speeds, so one falling behind must not cost the other events. A single shared buffer would make
/// the slowest reader the whole run's reader.
/// </para>
/// <para>
/// <strong>Bounded, dropping the oldest.</strong> The alternative on a full buffer is to block the
/// publisher, which is the run — an observer would then be able to slow down work by reading slowly,
/// or by stopping. Dropping the oldest keeps the most recent picture, which is what a progress view
/// is for, and the count of what was dropped travels with the subscription so the gap is visible.
/// </para>
/// <para>
/// <strong>Process-local, and that is a real limit.</strong> A watcher only sees a run executing on
/// the same instance, exactly like the run store it accompanies. The interface is the seam for a
/// shared implementation; nothing above it assumes in-process delivery.
/// </para>
/// </remarks>
public sealed class InMemoryRunProgressBroker : IRunProgressBroker
{
    private sealed class Subscription : IRunProgressSubscription
    {
        private readonly InMemoryRunProgressBroker _broker;
        private readonly string _jobId;
        private long _dropped;

        internal Subscription(InMemoryRunProgressBroker broker, string jobId, int capacity)
        {
            _broker = broker;
            _jobId = jobId;

            // DropOldest rather than Wait: TryWrite on a full DropOldest channel always succeeds, so
            // the publishing run never blocks and never has to care whether anyone is keeping up.
            Channel = System.Threading.Channels.Channel.CreateBounded<RunProgressEvent>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
        }

        internal Channel<RunProgressEvent> Channel { get; }

        public long DroppedCount => Interlocked.Read(ref _dropped);

        internal void RecordDrop() => Interlocked.Increment(ref _dropped);

        public IAsyncEnumerable<RunProgressEvent> ReadAllAsync(CancellationToken cancellationToken) =>
            Channel.Reader.ReadAllAsync(cancellationToken);

        public void Dispose()
        {
            _broker.Unsubscribe(_jobId, this);
            Channel.Writer.TryComplete();
        }
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Subscription, byte>> _watchers =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private int _openSubscriptions;

    /// <summary>Initializes the broker with the host's streaming limits and clock.</summary>
    public InMemoryRunProgressBroker(IOptionsMonitor<AppConfig> config, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);

        _config = config;
        _time = time;
    }

    /// <inheritdoc />
    public void Publish(
        string jobId,
        RunProgressKind kind,
        string? stepId = null,
        string? stepName = null,
        string? status = null,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        if (!_watchers.TryGetValue(jobId, out var subscribers) || subscribers.IsEmpty)
            return;

        // Numbered per run, and only for runs someone is watching. A gap in what a client receives
        // therefore means events were dropped for that client, not that the run skipped a number.
        var sequence = _sequences.AddOrUpdate(jobId, 1, static (_, current) => current + 1);

        var evt = new RunProgressEvent
        {
            JobId = jobId,
            Sequence = sequence,
            Kind = kind,
            OccurredAt = _time.GetUtcNow(),
            StepId = stepId,
            StepName = stepName,
            Status = status,
            Detail = detail
        };

        foreach (var subscription in subscribers.Keys)
        {
            // A full DropOldest channel evicts silently, so the eviction is counted here rather than
            // inferred: the reader compares its own count against the sequence numbers it saw.
            if (subscription.Channel.Reader.Count >= Capacity)
                subscription.RecordDrop();

            subscription.Channel.Writer.TryWrite(evt);
        }
    }

    /// <inheritdoc />
    public IRunProgressSubscription? Subscribe(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var limit = _config.CurrentValue.AI.WorkflowSubmission.MaxConcurrentProgressStreams;
        if (Interlocked.Increment(ref _openSubscriptions) > limit)
        {
            Interlocked.Decrement(ref _openSubscriptions);
            return null;
        }

        var subscription = new Subscription(this, jobId, Capacity);
        _watchers.GetOrAdd(jobId, static _ => new ConcurrentDictionary<Subscription, byte>())[subscription] = 0;

        return subscription;
    }

    private int Capacity => Math.Max(1, _config.CurrentValue.AI.WorkflowSubmission.ProgressBufferSize);

    private void Unsubscribe(string jobId, Subscription subscription)
    {
        if (_watchers.TryGetValue(jobId, out var subscribers) && subscribers.TryRemove(subscription, out _))
        {
            Interlocked.Decrement(ref _openSubscriptions);

            // The last watcher leaving takes the run's bookkeeping with it. Without this, a host that
            // has streamed many runs holds a sequence counter for every one of them forever.
            if (subscribers.IsEmpty)
            {
                _watchers.TryRemove(jobId, out _);
                _sequences.TryRemove(jobId, out _);
            }
        }
    }
}
