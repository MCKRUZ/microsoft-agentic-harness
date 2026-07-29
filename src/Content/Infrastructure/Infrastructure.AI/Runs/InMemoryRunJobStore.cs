using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Process-local <see cref="IRunJobStore"/>. One entry per run, expired on a retention clock.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Process-local, deliberately, and that is a real limit.</strong> A run is only visible to
/// the instance that accepted it, so a multi-instance deployment must route a caller's status polls
/// back to the instance that took the submission, or replace this with a shared store. Recorded here
/// rather than discovered later: the interface is the seam for that replacement, and nothing above it
/// assumes in-process storage.
/// </para>
/// <para>
/// <strong>Locking is per entry, not global.</strong> Claiming one run must not serialize status
/// reads of every other run — polling is the common operation and there will be far more of it than
/// claiming. The concurrent dictionary handles lookup; the per-entry lock makes each
/// read-decide-write atomic, which is what <see cref="TryBeginRun"/> needs to arm exactly once.
/// </para>
/// </remarks>
public sealed class InMemoryRunJobStore : IRunJobStore
{
    private sealed class Entry(RunRecord record, DateTimeOffset expiresAt)
    {
        public RunRecord Record { get; set; } = record;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;

    /// <summary>Initializes the store with the host's retention configuration and clock.</summary>
    public InMemoryRunJobStore(IOptionsMonitor<AppConfig> config, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);

        _config = config;
        _time = time;
    }

    private TimeSpan Ttl => _config.CurrentValue.AI.WorkflowSubmission.RunRecordTtl;

    /// <inheritdoc />
    public void Create(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A queued run is not yet reclaimable, so it is seeded with an expiry far enough out that it
        // cannot be swept while waiting; the real retention clock starts when it reaches a terminal
        // state. Seeding it with the TTL directly would let a run that sat in a long queue vanish
        // before it ever ran.
        var entry = new Entry(record, _time.GetUtcNow() + Ttl);

        if (!_entries.TryAdd(record.JobId, entry))
            throw new InvalidOperationException($"A run with job id '{record.JobId}' already exists.");
    }

    /// <inheritdoc />
    public RunRecord? Get(string jobId, string ownerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);

        if (!_entries.TryGetValue(jobId, out var entry))
            return null;

        lock (entry)
        {
            if (IsExpired(entry, _time.GetUtcNow()))
                return null;

            // Ordinal, matching how ownership is compared everywhere else in this solution. A
            // case-insensitive match here would let two identities that the rest of the system treats
            // as distinct read each other's runs.
            return string.Equals(entry.Record.OwnerId, ownerId, StringComparison.Ordinal)
                ? entry.Record
                : null;
        }
    }

    /// <inheritdoc />
    public RunRecord? TryBeginRun(string jobId, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        if (!_entries.TryGetValue(jobId, out var entry))
            return null;

        lock (entry)
        {
            // The whole claim is inside the lock: reading the status, deciding, and writing the new
            // one. Split across the lock boundary, two dispatchers could both observe Queued and both
            // proceed — and duplicate execution here is duplicate model and tool spend.
            if (entry.Record.Status != RunStatus.Queued || IsExpired(entry, _time.GetUtcNow()))
                return null;

            entry.Record = entry.Record with { Status = RunStatus.Running, StartedAt = startedAt };
            return entry.Record;
        }
    }

    /// <inheritdoc />
    public bool Update(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!_entries.TryGetValue(record.JobId, out var entry))
            return false;

        lock (entry)
        {
            entry.Record = record;

            // Retention starts when the run finishes, so a completed run is readable for a full TTL
            // from completion rather than from whenever it happened to be accepted.
            if (record.IsTerminal)
                entry.ExpiresAt = _time.GetUtcNow() + Ttl;
        }

        return true;
    }

    /// <inheritdoc />
    public int CountActiveRuns(string ownerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerId);

        var active = 0;
        foreach (var entry in _entries.Values)
        {
            lock (entry)
            {
                if (!entry.Record.IsTerminal
                    && string.Equals(entry.Record.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    active++;
                }
            }
        }

        return active;
    }

    /// <inheritdoc />
    public int SweepExpired()
    {
        var now = _time.GetUtcNow();
        var removed = 0;

        foreach (var (jobId, entry) in _entries)
        {
            bool expired;
            lock (entry)
            {
                expired = IsExpired(entry, now);
            }

            if (expired && _entries.TryRemove(jobId, out _))
                removed++;
        }

        return removed;
    }

    /// <summary>
    /// Whether an entry may be reclaimed. Only terminal runs expire — an accepted run that has not
    /// finished is never swept, however long it has been queued or running, because reclaiming it
    /// would make a run the caller is still polling silently disappear.
    /// </summary>
    private static bool IsExpired(Entry entry, DateTimeOffset now) =>
        entry.Record.IsTerminal && now >= entry.ExpiresAt;
}
