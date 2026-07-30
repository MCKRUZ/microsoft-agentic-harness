using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.KnowledgeGraph.Scoping;
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
/// <para>
/// <strong>Admission is the one exception, and takes a store-wide lock.</strong> Deciding it means
/// reading across entries — is this target already running, is this owner at capacity — so no
/// per-entry lock can make it atomic. It is serialized deliberately: starting a run is rare next to
/// polling one, and each admission is followed by an LLM workflow, so a scan of the entries costs
/// nothing measurable against what it gates. Held while the per-entry locks are taken, never the
/// reverse, so the two cannot deadlock.
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
    private readonly Lock _admission = new();
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
    public RunAdmission TryCreate(RunRecord record, int maxActiveRunsPerOwner)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_admission)
        {
            var (targetIsRunning, ownerActiveRuns) = SurveyActiveRuns(record);

            if (targetIsRunning)
                return RunAdmission.TargetAlreadyRunning;

            if (ownerActiveRuns >= maxActiveRunsPerOwner)
                return RunAdmission.OwnerAtCapacity;

            // The expiry seeded here is a placeholder that never elapses in practice: IsExpired only
            // reclaims terminal runs, and Update restamps the expiry the moment a run becomes one. It
            // exists so an entry always carries a value, not because a queued run is on a clock.
            var entry = new Entry(record, _time.GetUtcNow() + Ttl);

            if (!_entries.TryAdd(record.JobId, entry))
                throw new InvalidOperationException($"A run with job id '{record.JobId}' already exists.");

            return RunAdmission.Accepted;
        }
    }

    /// <summary>
    /// Answers both admission questions in one pass: whether the target of <paramref name="candidate"/>
    /// already has a live run, and how many live runs its caller holds.
    /// </summary>
    /// <remarks>
    /// The two questions are scoped differently on purpose. A target conflict is about the workflow's
    /// state, so it ignores who is asking — a second caller with access to the same workflow would
    /// corrupt the first caller's run just as surely. The capacity count is about the caller, so it is
    /// scoped to that principal by tenant and owner, the same pair every other read here compares.
    /// </remarks>
    private (bool TargetIsRunning, int OwnerActiveRuns) SurveyActiveRuns(RunRecord candidate)
    {
        var targetIsRunning = false;
        var ownerActiveRuns = 0;

        foreach (var entry in _entries.Values)
        {
            RunRecord record;
            lock (entry)
            {
                if (entry.Record.IsTerminal)
                    continue;

                record = entry.Record;
            }

            if (record.Kind == candidate.Kind
                && string.Equals(record.TargetId, candidate.TargetId, StringComparison.Ordinal))
            {
                targetIsRunning = true;
            }

            if (ScopeIdentity.AreSame(record.OwnerId, candidate.OwnerId)
                && ScopeIdentity.AreSame(record.TenantId, candidate.TenantId))
            {
                ownerActiveRuns++;
            }
        }

        return (targetIsRunning, ownerActiveRuns);
    }

    /// <inheritdoc />
    public RunRecord? Get(string jobId, string ownerId, string? tenantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);

        if (!_entries.TryGetValue(jobId, out var entry))
            return null;

        lock (entry)
        {
            if (IsExpired(entry, _time.GetUtcNow()))
                return null;

            // Tenant AND owner, canonicalized — the same two legs and the same canonical form that
            // decide plan ownership (PlannerScopeFilter.WritableBy) on this very request path. Owner
            // alone is sufficient while an issuer is pinned to one tenant, which is exactly why the
            // divergence would be invisible until the day it is a cross-tenant read.
            //
            // Canonicalized rather than compared strictly: a stricter comparison would deny a caller
            // its own run whenever a token differed only in casing from the one that started it,
            // while the plan store went on treating the two as the same principal.
            return ScopeIdentity.AreSame(entry.Record.OwnerId, ownerId)
                && ScopeIdentity.AreSame(entry.Record.TenantId, tenantId)
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

            entry.Record = entry.Record with
            {
                Status = RunStatus.Running,

                // First claim only. A run that parked on a gate and was resumed is claimed again, and
                // overwriting would report the run as having started after the approver answered —
                // hiding however long it ran before reaching the gate. ParkedAt is what tracks the
                // current wait; this tracks when the work began.
                StartedAt = entry.Record.StartedAt ?? startedAt
            };

            return entry.Record;
        }
    }

    /// <inheritdoc />
    public RunRecord? TryResume(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        if (!_entries.TryGetValue(jobId, out var entry))
            return null;

        lock (entry)
        {
            // Read, decide and write under one lock, exactly as TryBeginRun does: two resumers that
            // both saw the run parked would both enqueue it, and the second dispatch would run the same
            // plan alongside the first.
            if (!entry.Record.IsAwaitingDecision)
                return null;

            entry.Record = entry.Record with
            {
                Status = RunStatus.Queued,
                ParkedAt = null,
                AwaitingEscalationIds = []
            };

            return entry.Record;
        }
    }

    /// <inheritdoc />
    public RunRecord? TryCancel(string jobId, DateTimeOffset cancelledAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        if (!_entries.TryGetValue(jobId, out var entry))
            return null;

        lock (entry)
        {
            // Queued and parked only. A running run's status belongs to the dispatch executing it, and
            // a terminal one has already been answered — cancelling either here would overwrite a fact
            // with an intention.
            if (entry.Record.Status is not (RunStatus.Queued or RunStatus.Blocked))
                return null;

            var previous = entry.Record;

            entry.Record = previous with
            {
                Status = RunStatus.Cancelled,
                CompletedAt = cancelledAt,
                ParkedAt = null,
                AwaitingEscalationIds = []
            };

            // Terminal now, so retention runs from this moment — the same rule every other ending
            // follows.
            entry.ExpiresAt = cancelledAt + Ttl;

            return previous;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RunRecord> GetParkedRuns()
    {
        var parked = new List<RunRecord>();

        foreach (var entry in _entries.Values)
        {
            lock (entry)
            {
                if (entry.Record.IsAwaitingDecision)
                    parked.Add(entry.Record);
            }
        }

        return parked;
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
    public RunRecord? FindLiveRunForTarget(RunKind kind, string targetId)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);

        foreach (var entry in _entries.Values)
        {
            lock (entry)
            {
                if (entry.Record.IsTerminal)
                    continue;

                if (entry.Record.Kind == kind
                    && string.Equals(entry.Record.TargetId, targetId, StringComparison.Ordinal))
                {
                    return entry.Record;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ExpireStaleParkedRuns(TimeSpan maxParkedDuration)
    {
        if (maxParkedDuration <= TimeSpan.Zero)
            return [];

        var now = _time.GetUtcNow();
        var expired = new List<string>();

        foreach (var entry in _entries.Values)
        {
            lock (entry)
            {
                if (!entry.Record.IsAwaitingDecision)
                    continue;

                // A parked run with no ParkedAt cannot be aged, and guessing an age from CreatedAt
                // would expire a run that parked a moment ago on a workflow submitted last week. Left
                // alone deliberately: the stamp is written on the same update that sets the status, so
                // its absence means something wrote Blocked without going through the park path.
                var parkedAt = entry.Record.ParkedAt;
                if (parkedAt is null || now - parkedAt.Value < maxParkedDuration)
                    continue;

                entry.Record = entry.Record with
                {
                    Status = RunStatus.Failed,
                    Error = "The run was waiting for an approval that did not arrive in time.",
                    CompletedAt = now
                };

                // Now terminal, so retention applies from this moment — the same rule the ordinary
                // finishing path follows.
                entry.ExpiresAt = now + Ttl;
                expired.Add(entry.Record.JobId);
            }
        }

        return expired;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SweepExpired()
    {
        var now = _time.GetUtcNow();
        var removed = new List<string>();

        foreach (var (jobId, entry) in _entries)
        {
            // Decided and removed under the same lock. Only terminal entries expire today and nothing
            // re-arms a terminal entry, so splitting the two would be harmless right now — but it
            // would be a live race the moment any non-terminal state becomes reclaimable, and the
            // thing being raced away is a run a caller may still be polling.
            lock (entry)
            {
                if (!IsExpired(entry, now))
                    continue;

                if (_entries.TryRemove(jobId, out _))
                    removed.Add(jobId);
            }
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
