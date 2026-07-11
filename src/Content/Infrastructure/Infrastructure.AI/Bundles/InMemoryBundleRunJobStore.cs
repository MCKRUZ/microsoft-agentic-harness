using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Bundles;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// In-memory <see cref="IBundleRunJobStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// with a per-record TTL. Bundle runs are not persisted; a record lives only long enough for a caller to
/// poll its result, then the cleanup sweeper evicts it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What expires.</strong> A record is reclaimable only when it is terminal (its TTL then governs how
/// long the completed result stays pollable, starting from completion — so a caller gets the full window
/// regardless of how long the run queued or ran) <em>or</em> when it is an <em>unclaimed streaming
/// reservation</em>: a <see cref="BundleRunStatus.Queued"/> record with <see cref="BundleRunRecord.Streaming"/>
/// set, whose only driver is a caller opening the stream endpoint. Such a reservation may never be claimed
/// (the caller might never connect), so it is reclaimed once its window elapses to bound memory. Every other
/// non-terminal record — a background-queued run awaiting the dispatcher, or any run already
/// <see cref="BundleRunStatus.Running"/> — is never swept, so an in-flight run is never dropped or its outcome
/// lost to a mid-run sweep. This in-memory store does not survive a restart.
/// </para>
/// <para>
/// Each record's snapshot and expiry are guarded by a lock on its holder; in practice only the single
/// background dispatcher updates a given run, so contention is nil, but the lock keeps a concurrent sweep and
/// update consistent.
/// </para>
/// </remarks>
public sealed class InMemoryBundleRunJobStore : IBundleRunJobStore
{
    private sealed class JobEntry(BundleRunRecord record, DateTimeOffset expiresAt)
    {
        public BundleRunRecord Record { get; set; } = record;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }

    private readonly ConcurrentDictionary<string, JobEntry> _entries = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new <see cref="InMemoryBundleRunJobStore"/>.</summary>
    public InMemoryBundleRunJobStore(IOptionsMonitor<AppConfig> config, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);

        _config = config;
        _time = time;
    }

    private TimeSpan Ttl => _config.CurrentValue.AI.BundleExecution.RunRecordTtl;
    private TimeSpan StreamReservationTtl => _config.CurrentValue.AI.BundleExecution.StreamReservationTtl;

    /// <inheritdoc />
    public void Create(BundleRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A streaming reservation's initial expiry is the (separate, short) connect window — its own knob, so
        // tightening the completed-result retention window never shrinks how long a caller has to connect. It
        // is only consulted while the reservation is unclaimed; once claimed the run is in-flight and the
        // expiry is irrelevant. Every other record's initial expiry is the run-record window (which only
        // starts governing anything once the record is terminal).
        var initialExpiry = _time.GetUtcNow() + (record.Streaming ? StreamReservationTtl : Ttl);
        var entry = new JobEntry(record, initialExpiry);
        if (!_entries.TryAdd(record.JobId, entry))
            throw new InvalidOperationException($"A bundle run record with job id '{record.JobId}' already exists.");
    }

    /// <inheritdoc />
    public BundleRunRecord? Get(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        if (!_entries.TryGetValue(jobId, out var entry))
            return null;

        lock (entry)
        {
            return IsExpired(entry, _time.GetUtcNow()) ? null : entry.Record;
        }
    }

    /// <inheritdoc />
    public BundleRunRecord? TryBeginRun(string jobId, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        if (!_entries.TryGetValue(jobId, out var entry))
            return null;

        lock (entry)
        {
            // Refuse to claim a run that is not (still) an unexpired Queued reservation: another driver may have
            // already claimed it, it may have finished, or an unclaimed streaming reservation may have lapsed.
            if (entry.Record.Status != BundleRunStatus.Queued || IsExpired(entry, _time.GetUtcNow()))
                return null;

            entry.Record = entry.Record with { Status = BundleRunStatus.Running, StartedAt = startedAt };
            return entry.Record;
        }
    }

    /// <summary>
    /// A record is reclaimable when it is terminal (past its pollable window) or an unclaimed streaming
    /// reservation (a <see cref="BundleRunStatus.Queued"/> streaming run that was never picked up) past its
    /// window. Every other non-terminal record is retained. Callers must hold the entry lock.
    /// </summary>
    private static bool IsExpired(JobEntry entry, DateTimeOffset now)
    {
        var reclaimable = entry.Record.IsTerminal
            || (entry.Record.Streaming && entry.Record.Status == BundleRunStatus.Queued);
        return reclaimable && now >= entry.ExpiresAt;
    }

    /// <inheritdoc />
    public bool Update(BundleRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_entries.TryGetValue(record.JobId, out var entry))
            return false;

        lock (entry)
        {
            entry.Record = record;
            if (record.IsTerminal)
                entry.ExpiresAt = _time.GetUtcNow() + Ttl;
        }

        return true;
    }

    /// <inheritdoc />
    public int SweepExpired()
    {
        var now = _time.GetUtcNow();
        var evicted = 0;

        foreach (var (jobId, entry) in _entries)
        {
            bool expired;
            lock (entry)
            {
                expired = IsExpired(entry, now);
            }

            if (expired && _entries.TryRemove(new KeyValuePair<string, JobEntry>(jobId, entry)))
                evicted++;
        }

        return evicted;
    }
}
