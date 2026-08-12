using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Escalation;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Escalation;

/// <summary>
/// Thread-safe singleton implementation of <see cref="IApprovalFailureMemory"/>. Follows the same
/// bounded-LRU shape as <c>InProcessConversationBudgetTracker</c>.
/// </summary>
/// <remarks>
/// <strong>Cap is 2,000, not the 64 a single-user editor extension would use.</strong> This is a
/// singleton in a multi-tenant server process, not a per-user client — the property that makes
/// this feature useful (the interceptor outliving any one conversation) is exactly why a small
/// cap is wrong here: on a host with 100 concurrent conversations, a 64-entry LRU would evict a
/// conversation's memory within seconds and the feature would silently stop working with no
/// signal beyond an eviction warning nobody expected to need. 2,000 entries of a few short strings,
/// an int, and a Guid is a few hundred KB — cheap insurance against exactly that.
/// </remarks>
public sealed class InProcessApprovalFailureMemory : IApprovalFailureMemory
{
    /// <summary>Maximum number of keys tracked before least-recently-used eviction kicks in.</summary>
    internal const int MaxTrackedActions = 2_000;

    private readonly ConcurrentDictionary<ApprovalFailureKey, Entry> _entries = new();
    private readonly object _evictionLock = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InProcessApprovalFailureMemory> _logger;

    /// <summary>Creates the memory.</summary>
    /// <param name="timeProvider">Supplies the access timestamps that drive LRU eviction.</param>
    /// <param name="logger">Receives eviction warnings.</param>
    public InProcessApprovalFailureMemory(TimeProvider timeProvider, ILogger<InProcessApprovalFailureMemory> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public ApprovalFailureRecall? TryRecall(in ApprovalFailureKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
            return null;

        entry.LastAccessTicks = _timeProvider.GetUtcNow().UtcTicks;
        var (attemptCount, failureReason, escalationId) = entry.Snapshot();
        return new ApprovalFailureRecall(attemptCount, failureReason, escalationId);
    }

    /// <inheritdoc />
    public void RecordFailure(in ApprovalFailureKey key, string failureReason, Guid escalationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        var now = _timeProvider.GetUtcNow().UtcTicks;
        // Stamp the access time at creation so a brand-new entry is never rank-0 (oldest) for a
        // concurrent eviction running before this thread updates the timestamp.
        var entry = _entries.GetOrAdd(key, _ => new Entry { LastAccessTicks = now });
        entry.RecordFailure(failureReason, escalationId);
        entry.LastAccessTicks = now;
        // Deliberately not folded into the entry's own lock: LastAccessTicks is a pure ranking
        // signal, and two concurrent writers each stamping "now" in whichever order is a benign
        // race (LRU eviction only needs an approximate ordering, not one serialized with the
        // attempt data below).

        EvictIfOverCapacity();
    }

    /// <inheritdoc />
    public void Clear(in ApprovalFailureKey key) => _entries.TryRemove(key, out _);

    /// <summary>
    /// When the entry count exceeds the cap, evicts the least-recently-touched entries back down to
    /// ~90% of the cap in a single guarded pass, so concurrent writers don't each scan.
    /// </summary>
    private void EvictIfOverCapacity()
    {
        if (_entries.Count <= MaxTrackedActions)
            return;

        lock (_evictionLock)
        {
            if (_entries.Count <= MaxTrackedActions)
                return;

            var target = (int)(MaxTrackedActions * 0.9);
            var toRemove = _entries.Count - target;

            var oldest = _entries
                .OrderBy(kvp => kvp.Value.LastAccessTicks)
                .Take(toRemove)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldest)
                _entries.TryRemove(key, out _);

            _logger.LogWarning(
                "Approval failure memory evicted {Count} least-recently-used entries (cap {Cap})",
                oldest.Count, MaxTrackedActions);
        }
    }

    /// <summary>One key's recorded failure. <see cref="LastAccessTicks"/> drives LRU eviction.</summary>
    private sealed class Entry
    {
        // Guards AttemptCount/FailureReason/EscalationId as one unit — Snapshot() must never
        // return a mix of one RecordFailure call's count with another's reason or id, and Guid
        // is larger than a machine word so a lock-free write of it is not atomic on any runtime.
        // A per-entry lock is cheap here: this path is touched only when a human-approved call
        // fails, never on the hot tool-call path.
        private readonly object _gate = new();
        private long _lastAccessTicks;
        private int _attemptCount;
        private string _failureReason = string.Empty;
        private Guid _escalationId;

        /// <summary>
        /// Last access time in UTC ticks. Read/written via <see cref="Volatile"/>, independently of
        /// <see cref="_gate"/> — it is a pure LRU ranking signal, and eviction only needs an
        /// approximate ordering, not one serialized with the attempt data <see cref="_gate"/> guards.
        /// </summary>
        public long LastAccessTicks
        {
            get => Volatile.Read(ref _lastAccessTicks);
            set => Volatile.Write(ref _lastAccessTicks, value);
        }

        /// <summary>A coherent snapshot of the attempt count, failure reason, and escalation id together.</summary>
        public (int AttemptCount, string FailureReason, Guid EscalationId) Snapshot()
        {
            lock (_gate)
                return (_attemptCount, _failureReason, _escalationId);
        }

        public void RecordFailure(string failureReason, Guid escalationId)
        {
            lock (_gate)
            {
                _attemptCount++;
                _failureReason = failureReason;
                _escalationId = escalationId;
            }
        }
    }
}
