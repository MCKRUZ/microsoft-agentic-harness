using System.Collections.Concurrent;
using System.Threading.Channels;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.KnowledgeGraph.Scoping;
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
/// <strong>Capacity is bounded per caller as well as host-wide.</strong> A single global ceiling is a
/// ceiling any one caller can occupy: it opens streams to its own runs until every other tenant is
/// refused. The run substrate already bounds work per owner for the same reason, and streams follow
/// it rather than inventing a second, weaker rule.
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
        private readonly string _principal;
        private long _dropped;
        private int _disposed;

        internal Subscription(
            InMemoryRunProgressBroker broker, string jobId, string principal, int capacity)
        {
            _broker = broker;
            _jobId = jobId;
            _principal = principal;

            // Captured at subscribe time, not re-read per publish: the channel was created with this
            // bound, so comparing against a reloaded setting would mis-report drops for the life of
            // every subscription that predates the change.
            Capacity = capacity;

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

        internal int Capacity { get; }

        public long DroppedCount => Interlocked.Read(ref _dropped);

        internal void RecordDrop() => Interlocked.Increment(ref _dropped);

        public IAsyncEnumerable<RunProgressEvent> ReadAllAsync(CancellationToken cancellationToken) =>
            Channel.Reader.ReadAllAsync(cancellationToken);

        public void Dispose()
        {
            // Exactly once, whatever the caller does. The slot accounting is released here rather than
            // conditioned on finding this subscription still registered: a lookup that missed would
            // leak the slot permanently, and enough leaked slots refuse the endpoint to everyone.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _broker.Release(_jobId, _principal, this);
            Channel.Writer.TryComplete();
        }
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Subscription, byte>> _watchers =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingForget = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _perPrincipal = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private int _openSubscriptions;

    /// <summary>
    /// How many runs this broker still holds bookkeeping for.
    /// </summary>
    /// <remarks>
    /// Exposed so a test can assert that reclaiming actually happens. The count is the thing that
    /// grows without bound if it does not, and it is not observable from the outside any other way —
    /// a leak of this shape is invisible until a host has been up long enough to matter.
    /// </remarks>
    public int HeldRunCount => _watchers.Count;

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

        // Numbering and delivery happen together, per run. A plan runs steps concurrently, so two
        // threads can publish at once — and taking a number then writing separately lets the thread
        // that drew 4 write after the thread that drew 5. A watcher would then see 5 before 4 and, on
        // the documented reading of Sequence as position in the run's order, report a gap that never
        // happened. The lock is held only for the enqueue, which cannot block: a full buffer drops
        // rather than waits.
        //
        // The run's own subscriber dictionary IS the lock, rather than an entry in a second table
        // keyed by job id. A separate table has to be looked up again here, and that lookup can race
        // the sweep removing it: one publisher would then hold the old lock object while another
        // created and held a new one, and the two would number and deliver concurrently — losing
        // exactly the ordering this exists to guarantee. Both publishers necessarily hold the same
        // dictionary instance, because that is what they just read the subscribers out of.
        lock (subscribers)
        {
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
                // A full DropOldest channel evicts silently, so the eviction is counted here rather
                // than inferred: the reader compares its own count against the sequence numbers it
                // saw. The count is advisory — a reader draining concurrently can make it approximate
                // — and it is used to say "you missed some", never exactly how many.
                if (subscription.Channel.Reader.Count >= subscription.Capacity)
                    subscription.RecordDrop();

                subscription.Channel.Writer.TryWrite(evt);
            }
        }
    }

    /// <inheritdoc />
    public IRunProgressSubscription? Subscribe(string jobId, string ownerId, string? tenantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);

        var limits = _config.CurrentValue.AI.WorkflowSubmission;
        var principal = PrincipalKey(ownerId, tenantId);

        if (!TryReserve(principal, limits.MaxConcurrentProgressStreams, limits.MaxProgressStreamsPerOwner))
            return null;

        var subscription = new Subscription(this, jobId, principal, Math.Max(1, limits.ProgressBufferSize));
        _watchers.GetOrAdd(jobId, static _ => new ConcurrentDictionary<Subscription, byte>())[subscription] = 0;

        return subscription;
    }

    /// <summary>
    /// Takes one host slot and one caller slot together, giving both back if either is exhausted.
    /// </summary>
    private bool TryReserve(string principal, int hostLimit, int perOwnerLimit)
    {
        if (Interlocked.Increment(ref _openSubscriptions) > hostLimit)
        {
            Interlocked.Decrement(ref _openSubscriptions);
            return false;
        }

        var taken = _perPrincipal.AddOrUpdate(principal, 1, static (_, current) => current + 1);
        if (taken > perOwnerLimit)
        {
            ReleasePrincipal(principal);
            Interlocked.Decrement(ref _openSubscriptions);
            return false;
        }

        return true;
    }

    private void Release(string jobId, string principal, Subscription subscription)
    {
        // Accounting first and unconditionally — a slot must come back even if the bookkeeping below
        // finds nothing to prune.
        Interlocked.Decrement(ref _openSubscriptions);
        ReleasePrincipal(principal);

        if (!_watchers.TryGetValue(jobId, out var subscribers))
            return;

        subscribers.TryRemove(subscription, out _);

        // Pruning a live run's entry is deliberately NOT done here. Removing the per-run dictionary
        // after observing it empty is a check-then-act: a watcher subscribing in between is added to a
        // dictionary that is then unregistered, and it silently receives nothing for the rest of the
        // run. Reclaiming is the sweep's job, and it only ever runs for a run whose records are gone.
        //
        // The exception is a run the sweep already tried to forget while this watcher was attached.
        // Nothing will call Forget for it again, so the last watcher out completes what the sweep
        // deferred — and it is safe precisely because that run's records are gone, so nothing can
        // subscribe to or publish for it again.
        if (subscribers.IsEmpty && _pendingForget.TryRemove(jobId, out _))
            Purge(jobId);
    }

    private void ReleasePrincipal(string principal)
    {
        // Removed at zero rather than left holding a count of none, so the table is bounded by callers
        // streaming right now instead of by every caller that ever has.
        var remaining = _perPrincipal.AddOrUpdate(
            principal, 0, static (_, current) => current > 0 ? current - 1 : 0);

        if (remaining == 0)
            _perPrincipal.TryRemove(new KeyValuePair<string, int>(principal, 0));
    }

    /// <summary>
    /// Identifies the principal a stream is charged to, on the same two legs — tenant and owner —
    /// that decide whether it may read the run at all.
    /// </summary>
    private static string PrincipalKey(string ownerId, string? tenantId) =>
        $"{ScopeIdentity.Canonicalize(tenantId) ?? "-"}|{ScopeIdentity.Canonicalize(ownerId) ?? "-"}";

    /// <summary>
    /// Drops the bookkeeping for runs nobody is watching any more.
    /// </summary>
    /// <remarks>
    /// Separate from unsubscribing precisely so that unsubscribing does not have to decide whether a
    /// run is finished with. Called when run records are swept, by which point the run is terminal and
    /// no further events can be published for it.
    /// </remarks>
    public void Forget(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        if (_watchers.TryGetValue(jobId, out var subscribers) && !subscribers.IsEmpty)
        {
            // Deferred rather than dropped. This is called once per run, so returning here without a
            // record of having tried would leave the run's entries held for the life of the process —
            // the last watcher to leave completes it instead.
            _pendingForget[jobId] = 0;

            // Re-checked because that watcher may have left while the flag was being set, in which
            // case nobody else is coming to finish the job.
            if (!subscribers.IsEmpty || !_pendingForget.TryRemove(jobId, out _))
                return;
        }

        Purge(jobId);
    }

    private void Purge(string jobId)
    {
        _watchers.TryRemove(jobId, out _);
        _sequences.TryRemove(jobId, out _);
    }
}
