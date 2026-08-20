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
/// <para>
/// <strong>Admission is the one exception, and takes a store-wide lock.</strong> Deciding it means
/// reading across entries — is this conversation already running, is this owner at capacity — so no
/// per-entry lock can make it atomic. <c>_admission</c> is held while the per-entry locks are taken
/// during the survey, never the reverse, so the two cannot deadlock. Mirrors
/// <c>InMemoryRunJobStore</c>'s identical trade-off for workflow/plan runs.
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
    private readonly Lock _admission = new();
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
    public BundleRunAdmission TryCreate(BundleRunRecord record, int maxActiveRunsPerOwner)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_admission)
        {
            var (conversationIsRunning, ownerActiveRuns) = SurveyActiveRuns(record);

            if (conversationIsRunning)
                return BundleRunAdmission.ConversationAlreadyRunning;

            if (ownerActiveRuns >= maxActiveRunsPerOwner)
                return BundleRunAdmission.OwnerAtCapacity;

            // A streaming reservation's initial expiry is the (separate, short) connect window — its own knob,
            // so tightening the completed-result retention window never shrinks how long a caller has to
            // connect. It is only consulted while the reservation is unclaimed; once claimed the run is
            // in-flight and the expiry is irrelevant. Every other record's initial expiry is the run-record
            // window (which only starts governing anything once the record is terminal).
            var initialExpiry = _time.GetUtcNow() + (record.Streaming ? StreamReservationTtl : Ttl);
            var entry = new JobEntry(record, initialExpiry);
            if (!_entries.TryAdd(record.JobId, entry))
                throw new InvalidOperationException($"A bundle run record with job id '{record.JobId}' already exists.");

            return BundleRunAdmission.Accepted;
        }
    }

    /// <summary>
    /// Answers both admission questions in one pass: whether <paramref name="candidate"/>'s conversation
    /// (if any) already has a live run, and how many live runs its owner holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two questions are scoped differently on purpose. A conversation conflict is about that
    /// conversation's turn lease, so it ignores who is asking — a second caller sharing access to the
    /// same conversation would queue behind the lease just as surely. The capacity count is about the
    /// caller, so it is scoped to that owner alone (bundle runs carry no tenant).
    /// </para>
    /// <para>
    /// <strong>Uses <see cref="IsLive"/>, not <see cref="IsExpired"/> or bare
    /// <see cref="BundleRunRecord.IsTerminal"/>.</strong> The two are different questions.
    /// <see cref="IsExpired"/> answers "has this record's TTL window elapsed" — for a terminal record
    /// that window is the full poll-retention period, so reusing it here would count a run as live for
    /// as long as it stays pollable, not merely for as long as it is doing work. <see cref="IsLive"/>
    /// instead excludes a terminal record immediately, and additionally excludes an abandoned streaming
    /// reservation — non-terminal but reclaimable once its short connect window elapses (see the class
    /// remarks) — the moment it expires, not only once <see cref="SweepExpired"/> next runs. Without that
    /// second case, a caller who never opened a stream would permanently occupy a capacity slot and block
    /// every retry against its conversation until the next sweep, for a run that was never really live.
    /// </para>
    /// </remarks>
    private (bool ConversationIsRunning, int OwnerActiveRuns) SurveyActiveRuns(BundleRunRecord candidate)
    {
        var conversationIsRunning = false;
        var ownerActiveRuns = 0;
        var now = _time.GetUtcNow();

        foreach (var entry in _entries.Values)
        {
            BundleRunRecord record;
            lock (entry)
            {
                if (!IsLive(entry, now))
                    continue;

                record = entry.Record;
            }

            if (candidate.ConversationId is not null
                && string.Equals(record.ConversationId, candidate.ConversationId, StringComparison.Ordinal))
            {
                conversationIsRunning = true;
            }

            if (string.Equals(record.OwnerId, candidate.OwnerId, StringComparison.Ordinal))
                ownerActiveRuns++;
        }

        return (conversationIsRunning, ownerActiveRuns);
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
    /// Whether <paramref name="entry"/> is a <see cref="BundleRunStatus.Queued"/> streaming reservation
    /// nobody ever connected to. The one fact both <see cref="IsExpired"/> and <see cref="IsLive"/> need
    /// and must agree on; named once so a future reclaimable shape is added to only one of them by
    /// construction, not by remembering to touch both.
    /// </summary>
    private static bool IsUnclaimedStreamReservation(JobEntry entry)
        => entry.Record.Streaming && entry.Record.Status == BundleRunStatus.Queued;

    /// <summary>
    /// A record is reclaimable when it is terminal (past its pollable window) or an unclaimed streaming
    /// reservation (see <see cref="IsUnclaimedStreamReservation"/>) past its window. Every other
    /// non-terminal record is retained. Callers must hold the entry lock.
    /// </summary>
    private static bool IsExpired(JobEntry entry, DateTimeOffset now)
    {
        var reclaimable = entry.Record.IsTerminal || IsUnclaimedStreamReservation(entry);
        return reclaimable && now >= entry.ExpiresAt;
    }

    /// <summary>
    /// Whether <paramref name="entry"/> counts as a live run for admission purposes: not terminal, and not
    /// an abandoned streaming reservation past its (short) connect window. Distinct from
    /// <see cref="IsExpired"/>, which answers a different question — see <see cref="SurveyActiveRuns"/>'s
    /// remarks for why conflating the two undercounts how quickly a completed run frees its slot.
    /// Unlike <see cref="IsExpired"/>, a terminal record is never live regardless of <c>now</c> — its TTL
    /// governs only how long it stays <em>pollable</em>, not whether it still occupies a capacity slot.
    /// Callers must hold the entry lock.
    /// </summary>
    private static bool IsLive(JobEntry entry, DateTimeOffset now)
        => !entry.Record.IsTerminal
            && !(IsUnclaimedStreamReservation(entry) && now >= entry.ExpiresAt);

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
