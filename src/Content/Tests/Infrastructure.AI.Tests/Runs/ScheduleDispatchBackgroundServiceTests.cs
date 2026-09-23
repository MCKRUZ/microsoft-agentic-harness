using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Runs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="ScheduleDispatchBackgroundService"/>: that a due schedule fires exactly the
/// run it should, a disabled one never fires, and the two missed-run policies behave differently for
/// a genuine backlog miss — the exact distinction #593 exists to make precise.
/// </summary>
public sealed class ScheduleDispatchBackgroundServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Origin = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ScheduleDbContext> _dbOptions;
    private readonly ObservableFakeClock _clock = new(Origin);
    private readonly AppConfig _config = new();
    private readonly IRunJobStore _runStore;
    private readonly RecordingQueue _queue = new();
    private readonly EfScheduleStore _scheduleStore;

    public ScheduleDispatchBackgroundServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteVersionInterceptor())
            .Options;

        using (var ctx = new ScheduleDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        var factory = new TestDbContextFactory(_dbOptions);
        _scheduleStore = new EfScheduleStore(factory, new SchemaInitializer<ScheduleDbContext>(factory));

        _config.AI.Schedules.Enabled = true;
        _config.AI.Schedules.TickInterval = TimeSpan.FromMinutes(1);

        _runStore = new InMemoryRunJobStore(Options(), _clock);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ADueEnabledSchedule_FiresExactlyOneRun()
    {
        await CreateSchedule("s-1", nextFireAt: Origin);

        await StartAndWaitForFirstTimer();
        await AdvanceAndSettle(TimeSpan.FromMinutes(1));

        _queue.Enqueued.Should().HaveCount(1);
        var schedule = await _scheduleStore.GetAsync("s-1", "alice", null, CancellationToken.None);
        schedule!.LastFiredAt.Should().Be(_clock.GetUtcNow(), "the fire is stamped with the actual tick time, not the stale due time");
        schedule.NextFireAt.Should().BeAfter(Origin);
    }

    [Fact]
    public async Task ADisabledSchedule_NeverFires()
    {
        await CreateSchedule("s-2", nextFireAt: Origin, enabled: false);

        await StartAndWaitForFirstTimer();
        await AdvanceAndSettle(TimeSpan.FromMinutes(1));

        _queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task TickServiceDisabledHostWide_FiresNothing()
    {
        _config.AI.Schedules.Enabled = false;
        await CreateSchedule("s-3", nextFireAt: Origin);

        await StartAndWaitForFirstTimer();
        await AdvanceAndSettle(TimeSpan.FromMinutes(1));

        _queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task SkipPolicy_BacklogMiss_AdvancesWithoutFiring()
    {
        // NextFireAt two hours in the past on a 30-minute cadence: several occurrences missed, a
        // genuine backlog, not just this tick observing a schedule that's normally due right now.
        await CreateSchedule("s-skip", nextFireAt: Origin.AddHours(-2), missedRunPolicy: MissedRunPolicy.Skip);

        await StartAndWaitForFirstTimer();
        await AdvanceAndSettle(TimeSpan.FromMinutes(1));

        _queue.Enqueued.Should().BeEmpty("Skip must drop the backlog, not fire for it");
        var schedule = await _scheduleStore.GetAsync("s-skip", "alice", null, CancellationToken.None);
        schedule!.NextFireAt.Should().BeOnOrAfter(_clock.GetUtcNow(), "the schedule must resync to now, not stay stuck in the past");
    }

    [Fact]
    public async Task CatchUpOncePolicy_BacklogMiss_FiresExactlyOneRun()
    {
        await CreateSchedule("s-catchup", nextFireAt: Origin.AddHours(-2), missedRunPolicy: MissedRunPolicy.CatchUpOnce);

        await StartAndWaitForFirstTimer();
        await AdvanceAndSettle(TimeSpan.FromMinutes(1));

        _queue.Enqueued.Should().HaveCount(1,
            "CatchUpOnce must fire exactly one run for the whole backlog, never one per missed interval");
    }

    [Fact]
    public async Task ScheduleFiresAcrossARestart_WithoutDuplication()
    {
        // The literal acceptance criterion: a schedule fires on time across a host restart, without
        // firing twice. Modelled as two independent service instances sharing one store — the same
        // relationship a real restart has to the on-disk SQLite file.
        await CreateSchedule("s-restart", nextFireAt: Origin);

        await StartAndWaitForFirstTimer();
        await AdvanceAndSettle(TimeSpan.FromMinutes(1));
        _queue.Enqueued.Should().HaveCount(1, "the first instance fires the due schedule once");

        // "Restart": a second, independent service instance against the same durable store. Its own
        // internal state (if any) starts fresh; only what the store persisted survives.
        await StartAndWaitForFirstTimer();
        await AdvanceAndSettle(TimeSpan.FromMinutes(1));

        _queue.Enqueued.Should().HaveCount(1, "the schedule already advanced past due — the second instance must not re-fire it");
    }

    [Fact]
    public async Task AScheduleWithAnUnresolvableTimeZone_BacksOffInsteadOfSpinningEveryTick()
    {
        // Simulates external drift after creation (e.g. host tzdata changed) — the command validator
        // would refuse this at creation time, so this constructs the record directly through the store,
        // bypassing that check, the same way a real drift would arrive: valid at creation, broken later.
        await _scheduleStore.CreateAsync(new ScheduleRecord
        {
            ScheduleId = "s-broken-tz",
            Kind = RunKind.Workflow,
            TargetId = Guid.NewGuid().ToString(),
            OwnerId = "alice",
            Envelope = new CapabilityEnvelope(),
            CronExpression = "*/30 * * * *",
            TimeZoneId = "Not/A_Real_Zone",
            Enabled = true,
            NextFireAt = Origin,
            CreatedAt = Origin.AddDays(-1),
            Version = 0,
        }, CancellationToken.None);

        await StartAndWaitForFirstTimer();
        await AdvanceAndSettle(TimeSpan.FromMinutes(1));

        _queue.Enqueued.Should().BeEmpty("a schedule that cannot compute its next occurrence must never fire");

        var schedule = await _scheduleStore.GetAsync("s-broken-tz", "alice", null, CancellationToken.None);
        schedule!.NextFireAt.Should().BeAfter(
            _clock.GetUtcNow() + TimeSpan.FromMinutes(30),
            "it must back off well past the tick cadence, not stay due and get re-selected every tick forever");
    }

    private async Task CreateSchedule(
        string scheduleId, DateTimeOffset nextFireAt, bool enabled = true,
        MissedRunPolicy missedRunPolicy = MissedRunPolicy.Skip)
    {
        await _scheduleStore.CreateAsync(new ScheduleRecord
        {
            ScheduleId = scheduleId,
            Kind = RunKind.Workflow,
            TargetId = Guid.NewGuid().ToString(),
            OwnerId = "alice",
            Envelope = new CapabilityEnvelope(),
            CronExpression = "*/30 * * * *",
            TimeZoneId = "UTC",
            MissedRunPolicy = missedRunPolicy,
            Enabled = enabled,
            NextFireAt = nextFireAt,
            CreatedAt = Origin.AddDays(-1),
            Version = 0,
        }, CancellationToken.None);
    }

    private ScheduleDispatchBackgroundService CreateService() =>
        new(_scheduleStore, _runStore, _queue, Options(), _clock, NullLogger<ScheduleDispatchBackgroundService>.Instance);

    private IOptionsMonitor<AppConfig> Options()
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        return monitor.Object;
    }

    private ScheduleDispatchBackgroundService? _service;

    private async Task StartAndWaitForFirstTimer()
    {
        _service = CreateService();
        var armed = _clock.NextTimer;
        await _service.StartAsync(CancellationToken.None);
        await armed.WaitAsync(Patience);
    }

    private async Task AdvanceAndSettle(TimeSpan by)
    {
        var reArmed = _clock.NextTimer;
        _clock.Advance(by);
        await reArmed.WaitAsync(Patience);

        // One more settle pass: the tick's own async work (schedule store + run store I/O) happens
        // after the timer fires, so waiting only for the NEXT timer arm can race the tick's writes.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        if (_service is not null)
            await _service.StopAsync(CancellationToken.None);
    }

    private sealed class TestDbContextFactory(DbContextOptions<ScheduleDbContext> options)
        : IDbContextFactory<ScheduleDbContext>
    {
        public ScheduleDbContext CreateDbContext() => new(options);
    }

    /// <summary>Records what the tick service asked the dispatcher to run.</summary>
    private sealed class RecordingQueue : IRunDispatchQueue
    {
        private readonly ConcurrentQueue<string> _enqueued = new();

        public IReadOnlyList<string> Enqueued => [.. _enqueued];

        public ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken)
        {
            _enqueued.Enqueue(jobId);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Nothing drains the queue in these tests.");
    }

    /// <summary>A <see cref="FakeTimeProvider"/> that also says when something has started waiting on it.</summary>
    private sealed class ObservableFakeClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NextTimer => Volatile.Read(ref _next).Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);

            Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();

            return timer;
        }
    }
}
