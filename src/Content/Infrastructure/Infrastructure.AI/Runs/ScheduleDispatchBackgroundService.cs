using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Services.Runs;
using Domain.AI.Runs;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Ticks on a clock, claims every due schedule, and enqueues the run each one fires — the recurring
/// counterpart of <see cref="RunDispatchBackgroundService"/>, which only ever runs what a caller
/// explicitly submitted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Claims durably, unlike the rest of the run substrate.</strong>
/// <see cref="IScheduleStore.TryClaimAsync"/> is a real optimistic-concurrency write against a
/// database row, not the process-local lock <c>InMemoryRunJobStore.TryBeginRun</c> uses — so two
/// processes on the same machine sharing one SQLite file cannot both fire the same tick, and a
/// schedule survives a restart.
/// </para>
/// <para>
/// <strong>Writes directly to the run substrate rather than through MediatR.</strong>
/// <c>StartWorkflowRunCommand</c> handling is deliberately not reused here: that command resolves
/// ownership and an envelope from a live HTTP caller, and a tick has neither — the schedule's
/// creator was already authorized when the schedule was created (see <c>CreateScheduleCommand</c>'s
/// remarks), and it is that snapshot which every fire executes under.
/// </para>
/// <para>
/// <strong>Deliberately not built on <c>PeriodicSweepService</c>,</strong> despite <c>ExecuteAsync</c>
/// below sharing that base class's "read interval, clamp to a floor, TimeProvider-delay, re-check
/// enabled after the wait" loop shape. The real divergence: <c>PeriodicSweepService.SweepOnceAsync</c>
/// deliberately runs its sweep with <see cref="CancellationToken.None"/> so an in-flight sweep always
/// finishes even during shutdown, whereas <see cref="RunTickAsync"/> deliberately checks its
/// <c>stoppingToken</c> per schedule so shutdown stops firing new ones immediately —
/// firing an already-claimed run is cheap to leave in flight, but claiming and firing several more
/// schedules after shutdown was requested is not a behavior this service wants. That divergence, not
/// a difference in return shape, is why this stays its own loop rather than a base-class override.
/// </para>
/// </remarks>
public sealed class ScheduleDispatchBackgroundService : BackgroundService
{
    /// <summary>
    /// How long a schedule that failed to compute its next occurrence (an unresolvable time zone, an
    /// unsatisfiable active-hours window) is pushed out before being retried. Without this, such a
    /// schedule would stay permanently "due" and re-fail identically on every tick, forever, at the
    /// tick cadence rather than backing off.
    /// </summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// Floor on <see cref="Domain.Common.Config.AI.Schedules.ScheduleConfig.TickInterval"/> this
    /// service enforces regardless of configuration — matching every other periodic service here
    /// (<c>RunRecordCleanupService</c>, <c>ParkedRunResumeService</c>,
    /// <c>ConversationBudgetRetentionService</c>, <c>ToolResultRetentionService</c>,
    /// <c>BundleWorkspaceCleanupService</c>), each of which owns its own floor rather than reading
    /// one off the Domain config type it enforces against.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly IScheduleStore _scheduleStore;
    private readonly IRunJobStore _runStore;
    private readonly IRunDispatchQueue _queue;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScheduleDispatchBackgroundService> _logger;

    public ScheduleDispatchBackgroundService(
        IScheduleStore scheduleStore,
        IRunJobStore runStore,
        IRunDispatchQueue queue,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<ScheduleDispatchBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(scheduleStore);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scheduleStore = scheduleStore;
        _runStore = runStore;
        _queue = queue;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = _config.CurrentValue.AI.Schedules.TickInterval;
                if (interval < MinInterval)
                    interval = MinInterval;

                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);

                if (!_config.CurrentValue.AI.Schedules.Enabled)
                    continue;

                await RunTickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — expected.
        }
    }

    private async Task RunTickAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<ScheduleRecord> due;
        DateTimeOffset now;
        try
        {
            now = _timeProvider.GetUtcNow();
            due = await _scheduleStore.GetDueSchedulesAsync(now, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Reading the due list itself failed (e.g. the database is briefly unreachable) — nothing
            // to fire this tick either way.
            _logger.LogError(ex, "Could not read due schedules; will retry on the next interval.");
            return;
        }

        foreach (var schedule in due)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await FireScheduleAsync(schedule, now, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fires one schedule, isolating its fault from every other due schedule in the same tick. Without
    /// this, an exception firing schedule N (a claim conflict aside, which <see cref="FireOnceAsync"/>
    /// already handles) would abort the loop and leave every schedule after it in the tick's due list
    /// unprocessed until the next tick — one broken schedule silently starving every other one queued
    /// beside it.
    /// </summary>
    private async Task FireScheduleAsync(ScheduleRecord schedule, DateTimeOffset now, CancellationToken stoppingToken)
    {
        try
        {
            await FireOnceAsync(schedule, now, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Firing schedule {ScheduleId} failed; other due schedules in this tick still proceed.",
                schedule.ScheduleId);
        }
    }

    private async Task FireOnceAsync(ScheduleRecord schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // HasMissedMultipleOccurrences and ComputeNextFireAt both resolve the same cron
        // expression/time zone, so both must be inside this one try: an invalid time zone throws from
        // the FIRST call, and moving it outside would let that exception escape uncaught before the
        // backoff below ever runs — leaving NextFireAt untouched, exactly the spin this guards against.
        bool isBacklogMiss;
        DateTimeOffset nextFireAt;
        try
        {
            isBacklogMiss = ScheduleOccurrenceCalculator.HasMissedMultipleOccurrences(
                schedule.CronExpression, schedule.TimeZoneId, schedule.NextFireAt, now);
            nextFireAt = ComputeNextFireAt(schedule, now, isBacklogMiss);
        }
        catch (Exception ex)
        {
            await BackOffAfterComputeFailureAsync(schedule, now, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (isBacklogMiss && schedule.MissedRunPolicy == MissedRunPolicy.Skip)
        {
            // Advances past the backlog without firing. Still goes through TryClaimAsync so a
            // concurrent process racing this same tick cannot also decide to skip-and-advance —
            // only the winner adjusts NextFireAt.
            await _scheduleStore.TryClaimAsync(
                schedule.ScheduleId, schedule.Version, nextFireAt, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (isBacklogMiss && schedule.MissedRunPolicy == MissedRunPolicy.CatchUpOnce
            && !ScheduleOccurrenceCalculator.IsWithinActiveHours(
                now, schedule.TimeZoneId, schedule.ActiveHoursStart, schedule.ActiveHoursEnd))
        {
            // CatchUpOnce fires its one recovery run at `now`, not at a recomputed occurrence — so
            // unlike the normal due-schedule path below, nothing upstream has already confirmed `now`
            // itself respects the active-hours window declared for this schedule. Firing outside it
            // would violate the window's whole purpose (e.g. a host down overnight recovering at 3am
            // against a 9-5 window). `nextFireAt` was already computed respecting the window, so the
            // schedule still advances correctly; the catch-up is forfeited, same as Skip would do.
            await _scheduleStore.TryClaimAsync(
                schedule.ScheduleId, schedule.Version, nextFireAt, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        var won = await _scheduleStore.TryClaimAsync(
            schedule.ScheduleId, schedule.Version, nextFireAt, now, cancellationToken).ConfigureAwait(false);

        if (!won)
        {
            _logger.LogDebug(
                "Schedule {ScheduleId} was not claimable; another process won this tick.", schedule.ScheduleId);
            return;
        }

        // TryClaimAsync reports success as a bare bool rather than the updated row — see its own
        // remarks for why — so the claimed record is built here from the pre-claim schedule plus the
        // three fields a claim changes; everything else about a schedule is untouched by claiming it.
        var claimed = schedule with { NextFireAt = nextFireAt, LastFiredAt = now, Version = schedule.Version + 1 };
        await EnqueueRunAsync(claimed, now).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes when this fire's occurrence advances to. Strictly after the tick being consumed —
    /// <c>ComputeNextOccurrence</c>'s search is inclusive of its starting point, so searching from
    /// exactly <c>schedule.NextFireAt</c> (or <paramref name="now"/>, if that also happens to land on
    /// a valid cron tick) would find the SAME occurrence again and the schedule would never advance,
    /// firing every tick forever. One tick (100ns) is far below any realistic cron granularity
    /// (minutes), so it reliably means "strictly after" here.
    /// </summary>
    private static DateTimeOffset ComputeNextFireAt(ScheduleRecord schedule, DateTimeOffset now, bool isBacklogMiss)
    {
        var reference = (isBacklogMiss ? now : schedule.NextFireAt).AddTicks(1);
        var cooldownFloor = now + schedule.Cooldown;
        var earliestAllowed = reference > cooldownFloor ? reference : cooldownFloor;

        return ScheduleOccurrenceCalculator.ComputeNextOccurrence(
            schedule.CronExpression, schedule.TimeZoneId, earliestAllowed,
            schedule.ActiveHoursStart, schedule.ActiveHoursEnd);
    }

    /// <summary>
    /// The command validator confirmed this schedule's expression/timezone/window at creation time, so
    /// reaching here means something external changed (e.g. the host's IANA tzdata) or the active-hours
    /// window stopped being satisfiable. Left with <c>NextFireAt</c> untouched, this schedule would stay
    /// "due" and be re-selected — and re-fail identically — on every single tick forever, spamming this
    /// error at the tick cadence (as fast as every 30s by default) with no backoff. Pushed out by
    /// <see cref="FailureBackoff"/> instead: the schedule still cannot recompute a real occurrence, but
    /// it stops monopolizing every tick while it can't.
    /// </summary>
    private async Task BackOffAfterComputeFailureAsync(
        ScheduleRecord schedule, DateTimeOffset now, Exception ex, CancellationToken cancellationToken)
    {
        _logger.LogError(ex,
            "Could not compute the next occurrence for schedule {ScheduleId}; retrying in {Backoff}.",
            schedule.ScheduleId, FailureBackoff);

        await _scheduleStore.TryClaimAsync(
            schedule.ScheduleId, schedule.Version, now + FailureBackoff, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Admits and queues the run a fired schedule produces, mirroring
    /// <c>StartWorkflowRunCommandHandler</c>'s own admission+enqueue shape.
    /// </summary>
    private async Task EnqueueRunAsync(ScheduleRecord schedule, DateTimeOffset firedAt)
    {
        var record = BuildRunRecord(schedule, firedAt);

        if (!TryAdmitRun(schedule, record))
            return;

        await EnqueueOrMarkFailedAsync(schedule, record, firedAt).ConfigureAwait(false);
    }

    private static RunRecord BuildRunRecord(ScheduleRecord schedule, DateTimeOffset firedAt) => new()
    {
        JobId = Guid.NewGuid().ToString("N"),
        Kind = schedule.Kind,
        TargetId = schedule.TargetId,
        OwnerId = schedule.OwnerId,
        TenantId = schedule.TenantId,
        Envelope = schedule.Envelope,
        Status = RunStatus.Queued,
        CreatedAt = firedAt,
    };

    /// <summary>
    /// True when <paramref name="record"/> was admitted into the run substrate. False (logged, not
    /// thrown) on refusal: <see cref="RunAdmission.TargetAlreadyRunning"/> is expected under normal
    /// operation — a fast-firing schedule whose previous run overran its own interval; anything else
    /// means the owner is genuinely at its run ceiling, worth a louder log.
    /// </summary>
    private bool TryAdmitRun(ScheduleRecord schedule, RunRecord record)
    {
        var maxConcurrentRuns = _config.CurrentValue.AI.WorkflowSubmission.MaxConcurrentRunsPerOwner;
        var admission = _runStore.TryCreate(record, maxConcurrentRuns);
        if (admission == RunAdmission.Accepted)
            return true;

        if (admission == RunAdmission.TargetAlreadyRunning)
        {
            _logger.LogDebug(
                "Schedule {ScheduleId} fired but its target already has a run in progress; this tick's run is skipped.",
                schedule.ScheduleId);
        }
        else
        {
            _logger.LogWarning(
                "Schedule {ScheduleId} fired but its run was not admitted ({Admission}); this tick's run is lost.",
                schedule.ScheduleId, admission);
        }

        return false;
    }

    private async Task EnqueueOrMarkFailedAsync(ScheduleRecord schedule, RunRecord record, DateTimeOffset firedAt)
    {
        try
        {
            // Deliberately not the tick's own token — matching StartWorkflowRunCommandHandler, the
            // record is committed past admission, and a record committed but never queued is stranded.
            await _queue.EnqueueAsync(record.JobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {JobId} fired by schedule {ScheduleId} was accepted but could not be queued.",
                record.JobId, schedule.ScheduleId);
            _runStore.Update(record with
            {
                Status = RunStatus.Failed,
                Error = "The run was accepted but could not be queued for execution.",
                CompletedAt = firedAt,
            });
            return;
        }

        _logger.LogInformation(
            "Schedule {ScheduleId} fired run {JobId} for {Kind} {TargetId}.",
            schedule.ScheduleId, record.JobId, record.Kind, record.TargetId);
    }
}
