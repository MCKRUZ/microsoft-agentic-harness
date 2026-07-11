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
/// <strong>Only terminal records expire.</strong> A <see cref="BundleRunStatus.Queued"/> or
/// <see cref="BundleRunStatus.Running"/> record is in-flight and is never expired or swept — otherwise a run
/// still queued behind a slow predecessor, or one executing longer than the TTL, could have its record
/// deleted out from under it (the dispatcher would then find nothing to update and silently drop a completed
/// outcome). The TTL therefore governs only how long a completed run stays pollable: it starts when the
/// record reaches a terminal state, so a caller gets the full configured window to read the result regardless
/// of how long the run queued or ran first. A run that never reaches a terminal state is retained until the
/// process exits; in practice the dispatcher always drives a queued run to a terminal state, and this
/// in-memory store does not survive a restart.
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

    /// <inheritdoc />
    public void Create(BundleRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var entry = new JobEntry(record, _time.GetUtcNow() + Ttl);
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
            // Non-terminal records never expire; a terminal record expires once its pollable window elapses.
            return entry.Record.IsTerminal && _time.GetUtcNow() >= entry.ExpiresAt ? null : entry.Record;
        }
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
                // Only terminal records are reclaimable — an in-flight run is never swept.
                expired = entry.Record.IsTerminal && now >= entry.ExpiresAt;
            }

            if (expired && _entries.TryRemove(new KeyValuePair<string, JobEntry>(jobId, entry)))
                evicted++;
        }

        return evicted;
    }
}
