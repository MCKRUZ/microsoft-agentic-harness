using Domain.AI.Bundles;
using Domain.AI.Runs;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Runs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="EfScheduleStore"/>, centered on <see cref="EfScheduleStore.TryClaimAsync"/> —
/// the durable claim-once primitive the tick service relies on to fire a schedule exactly once,
/// across any number of processes sharing one SQLite file.
/// </summary>
public sealed class EfScheduleStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ScheduleDbContext> _options;
    private readonly EfScheduleStore _store;

    public EfScheduleStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteVersionInterceptor())
            .Options;

        using var ctx = new ScheduleDbContext(_options);
        ctx.Database.EnsureCreated();

        var factory = new TestDbContextFactory(_options);
        _store = new EfScheduleStore(factory, new SchemaInitializer<ScheduleDbContext>(factory));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task TryClaimAsync_TwoConcurrentClaims_ExactlyOneWins()
    {
        var record = NewRecord("s-1");
        await _store.CreateAsync(record, CancellationToken.None);

        var stored = await _store.GetAsync("s-1", "alice", null, CancellationToken.None);
        stored.Should().NotBeNull();

        var nextFireAt = stored!.NextFireAt.AddMinutes(30);
        var firedAt = stored.NextFireAt;

        var results = await Task.WhenAll(
            _store.TryClaimAsync("s-1", stored.Version, nextFireAt, firedAt, CancellationToken.None),
            _store.TryClaimAsync("s-1", stored.Version, nextFireAt, firedAt, CancellationToken.None));

        results.Count(r => r).Should().Be(1, "only the process that wins the version race may enqueue this tick's run");
        results.Count(r => !r).Should().Be(1, "the loser must get false, not a silent duplicate");
    }

    [Fact]
    public async Task TryClaimAsync_StaleVersion_ReturnsFalse()
    {
        var record = NewRecord("s-2");
        await _store.CreateAsync(record, CancellationToken.None);

        var claimed = await _store.TryClaimAsync(
            "s-2", record.Version, record.NextFireAt.AddMinutes(30), record.NextFireAt, CancellationToken.None);
        claimed.Should().BeTrue();

        // Retrying with the ORIGINAL (now stale) version must fail — this is the exact shape a
        // straggling process racing the winner above would hit.
        var staleRetry = await _store.TryClaimAsync(
            "s-2", record.Version, record.NextFireAt.AddMinutes(60), record.NextFireAt, CancellationToken.None);

        staleRetry.Should().BeFalse();
    }

    [Fact]
    public async Task TryClaimAsync_UnknownSchedule_ReturnsFalse()
    {
        var claimed = await _store.TryClaimAsync(
            "does-not-exist", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None);

        claimed.Should().BeFalse();
    }

    [Fact]
    public async Task GetDueSchedulesAsync_OnlyReturnsEnabledSchedulesAtOrBeforeNow()
    {
        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

        var due = NewRecord("due", nextFireAt: now);
        var notYetDue = NewRecord("not-yet-due", nextFireAt: now.AddMinutes(30));
        var disabled = NewRecord("disabled", nextFireAt: now) with { Enabled = false };

        await _store.CreateAsync(due, CancellationToken.None);
        await _store.CreateAsync(notYetDue, CancellationToken.None);
        await _store.CreateAsync(disabled, CancellationToken.None);

        var result = await _store.GetDueSchedulesAsync(now, CancellationToken.None);

        result.Select(r => r.ScheduleId).Should().BeEquivalentTo(["due"]);
    }

    [Fact]
    public async Task ListForOwnerAsync_DoesNotReturnAnotherOwnersSchedules()
    {
        await _store.CreateAsync(NewRecord("alice-1", ownerId: "alice"), CancellationToken.None);
        await _store.CreateAsync(NewRecord("bob-1", ownerId: "bob"), CancellationToken.None);

        var aliceSchedules = await _store.ListForOwnerAsync("alice", null, CancellationToken.None);

        aliceSchedules.Select(r => r.ScheduleId).Should().BeEquivalentTo(["alice-1"]);
    }

    [Fact]
    public async Task PauseAsync_StaleVersion_ReturnsFalseAndLeavesScheduleEnabled()
    {
        var record = NewRecord("s-pause");
        await _store.CreateAsync(record, CancellationToken.None);

        var paused = await _store.PauseAsync("s-pause", "alice", null, expectedVersion: 99, CancellationToken.None);

        paused.Should().BeFalse();
        (await _store.GetAsync("s-pause", "alice", null, CancellationToken.None))!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task PauseAsync_TwoConcurrentCalls_ExactlyOneWins()
    {
        // Mirrors TryClaimAsync_TwoConcurrentClaims_ExactlyOneWins — SetEnabledAsync (which backs both
        // PauseAsync and ResumeAsync) went through the same load-then-save-to-ExecuteUpdateAsync change,
        // so it needs the same concurrent-write proof.
        var record = NewRecord("s-pause-race");
        await _store.CreateAsync(record, CancellationToken.None);

        var results = await Task.WhenAll(
            _store.PauseAsync("s-pause-race", "alice", null, record.Version, CancellationToken.None),
            _store.PauseAsync("s-pause-race", "alice", null, record.Version, CancellationToken.None));

        results.Count(r => r).Should().Be(1, "only the process that wins the version race may pause the schedule");
        results.Count(r => !r).Should().Be(1, "the loser must get false, not a silent duplicate write");

        var stored = await _store.GetAsync("s-pause-race", "alice", null, CancellationToken.None);
        stored!.Version.Should().Be(record.Version + 1, "the version must advance by exactly one, not once per caller");
    }

    [Fact]
    public async Task DeleteAsync_AnotherOwnersSchedule_ReturnsFalse()
    {
        await _store.CreateAsync(NewRecord("s-del", ownerId: "alice"), CancellationToken.None);

        var deleted = await _store.DeleteAsync("s-del", "bob", null, CancellationToken.None);

        deleted.Should().BeFalse();
        (await _store.GetAsync("s-del", "alice", null, CancellationToken.None)).Should().NotBeNull();
    }

    private static ScheduleRecord NewRecord(
        string scheduleId, string ownerId = "alice", DateTimeOffset? nextFireAt = null) => new()
    {
        ScheduleId = scheduleId,
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = ownerId,
        Envelope = new CapabilityEnvelope(),
        CronExpression = "*/30 * * * *",
        TimeZoneId = "UTC",
        Enabled = true,
        NextFireAt = nextFireAt ?? new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero),
        CreatedAt = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero),
        Version = 0,
    };

    private sealed class TestDbContextFactory(DbContextOptions<ScheduleDbContext> options)
        : IDbContextFactory<ScheduleDbContext>
    {
        public ScheduleDbContext CreateDbContext() => new(options);
    }
}
