using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The retention sweep on <see cref="SqliteConversationBudgetTracker"/> (issue #253).
/// </summary>
/// <remarks>
/// <para>
/// A budget row records how much of a lifetime ceiling a conversation has spent, so deleting the wrong
/// one hands that conversation a fresh ceiling with nothing erroring — the exact failure the budget
/// exists to prevent, caused by the thing meant to tidy up after it. These tests are therefore weighted
/// towards what must <em>survive</em> a sweep rather than what must be removed.
/// </para>
/// <para>
/// Against a real SQLite file, like the rest of this store's tests: the sweep is one correlated SQL
/// statement across two tables, and an in-memory fake of either would test the fake.
/// </para>
/// </remarks>
public sealed class SqliteConversationBudgetSweepTests : IDisposable
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(30);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly TestConversationDbContextFactory _contextFactory;
    private readonly FakeTimeProvider _clock = new(Origin);
    private readonly SqliteConversationBudgetTracker _tracker;

    /// <summary>Creates an isolated on-disk database and the tracker under test.</summary>
    public SqliteConversationBudgetSweepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convsweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _databasePath = Path.Combine(_tempDir, "conversations.db");
        _contextFactory = new TestConversationDbContextFactory(_databasePath);

        var config = new AppConfig();
        config.AI.AgentFramework.ConversationTokenBudget = 10_000;

        _tracker = new SqliteConversationBudgetTracker(
            _contextFactory,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            _clock,
            NullLogger<SqliteConversationBudgetTracker>.Instance,
            new SchemaInitializer<ConversationDbContext>(_contextFactory));
    }

    /// <summary>
    /// The case the sweep exists for: a conversation was deleted and its running total stayed behind.
    /// </summary>
    [Fact]
    public async Task SweepAbandonedAsync_ConversationWasDeleted_ReclaimsItsRow()
    {
        const string key = "conv-deleted";
        GivenConversation(key);
        await _tracker.RecordUsageAsync(key, 500);

        // Control. Without it, a sweep that deletes everything — or a tracker that never wrote the row
        // in the first place — would satisfy the assertion below just as well as a correct one.
        BudgetKeys().Should().Contain(key, "control: the row must exist before a sweep can be said to remove it");

        DeleteConversation(key);
        _clock.Advance(Grace + TimeSpan.FromDays(1));

        var reclaimed = await _tracker.SweepAbandonedAsync(Grace);

        reclaimed.Should().Be(1);
        BudgetKeys().Should().NotContain(key);
    }

    /// <summary>
    /// The load-bearing one. A conversation that merely sits idle keeps its total for as long as it
    /// exists, no matter how long that is.
    /// </summary>
    /// <remarks>
    /// An age-only sweep would pass every other test in this file and fail here, and the damage it does
    /// is invisible: the user returns after a long absence, their ceiling has silently reset, and the
    /// conversation runs to twice the configured length with nothing logged. Ten years, deliberately —
    /// any threshold someone might later think defensible is well inside it.
    /// </remarks>
    [Fact]
    public async Task SweepAbandonedAsync_ConversationStillExists_KeepsItsRowHoweverOld()
    {
        const string key = "conv-idle-for-years";
        GivenConversation(key);
        await _tracker.RecordUsageAsync(key, 500);

        _clock.Advance(TimeSpan.FromDays(365 * 10));

        var reclaimed = await _tracker.SweepAbandonedAsync(Grace);

        reclaimed.Should().Be(0);
        BudgetKeys().Should().Contain(key,
            "a conversation that still exists can still be resumed, and removing its total would hand it "
            + "a fresh ceiling — the silent reset the whole budget exists to prevent");
        (await _tracker.GetStatusAsync(key)).ConsumedTokens.Should().Be(500,
            "surviving the sweep must mean the total survived, not merely that some row did");
    }

    /// <summary>
    /// A budget key with no conversation is not necessarily abandoned — it may belong to an in-flight
    /// plan run, or to a caller nobody has written yet. The grace period is what protects those.
    /// </summary>
    [Fact]
    public async Task SweepAbandonedAsync_PlanRunKeyWithinTheGracePeriod_IsKept()
    {
        const string key = "planrun:in-flight";
        await _tracker.RecordUsageAsync(key, 300);

        // Old enough to look abandoned by any casual reading, still comfortably inside the window.
        _clock.Advance(Grace - TimeSpan.FromDays(1));

        var reclaimed = await _tracker.SweepAbandonedAsync(Grace);

        reclaimed.Should().Be(0);
        BudgetKeys().Should().Contain(key,
            "no conversation row exists for a plan run, so the existence test alone would delete the "
            + "budget of an operation that is still running");
    }

    /// <summary>
    /// The same key once the window has passed. Together with the test above this pins the grace period
    /// as the thing deciding, rather than leaving both outcomes explainable by the key's shape.
    /// </summary>
    [Fact]
    public async Task SweepAbandonedAsync_PlanRunKeyBeyondTheGracePeriod_IsReclaimed()
    {
        const string key = "planrun:leaked";
        await _tracker.RecordUsageAsync(key, 300);

        _clock.Advance(Grace + TimeSpan.FromDays(1));

        var reclaimed = await _tracker.SweepAbandonedAsync(Grace);

        reclaimed.Should().Be(1);
        BudgetKeys().Should().NotContain(key);
    }

    /// <summary>
    /// One sweep must not be able to take a live conversation with it. Runs both cases through a single
    /// statement, which is where a mistaken join or a stray <c>OR</c> would show.
    /// </summary>
    [Fact]
    public async Task SweepAbandonedAsync_MixedTable_ReclaimsOnlyTheOrphans()
    {
        GivenConversation("live-a");
        GivenConversation("live-b");
        GivenConversation("deleted-c");

        foreach (var key in new[] { "live-a", "live-b", "deleted-c", "planrun:old" })
            await _tracker.RecordUsageAsync(key, 100);

        DeleteConversation("deleted-c");
        _clock.Advance(Grace + TimeSpan.FromDays(1));

        var reclaimed = await _tracker.SweepAbandonedAsync(Grace);

        reclaimed.Should().Be(2);
        BudgetKeys().Should().BeEquivalentTo(["live-a", "live-b"]);
    }

    /// <summary>
    /// A zero grace period is a valid configuration and must still respect the existence test — the two
    /// conditions are an AND, and a sweep that treated zero as "delete everything old" would be an OR.
    /// </summary>
    [Fact]
    public async Task SweepAbandonedAsync_ZeroGracePeriod_StillKeepsLiveConversations()
    {
        GivenConversation("live");
        await _tracker.RecordUsageAsync("live", 100);
        await _tracker.RecordUsageAsync("orphan", 100);

        _clock.Advance(TimeSpan.FromSeconds(1));

        var reclaimed = await _tracker.SweepAbandonedAsync(TimeSpan.Zero);

        reclaimed.Should().Be(1);
        BudgetKeys().Should().BeEquivalentTo(["live"]);
    }

    /// <summary>A negative grace period would move the cutoff into the future; refuse it outright.</summary>
    [Fact]
    public async Task SweepAbandonedAsync_NegativeGracePeriod_Throws()
    {
        var sweep = async () => await _tracker.SweepAbandonedAsync(TimeSpan.FromDays(-1));

        await sweep.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private void GivenConversation(string id)
    {
        using var context = _contextFactory.CreateDbContext();
        context.Conversations.Add(new ConversationEntity
        {
            Id = id,
            AgentName = "agent",
            UserId = "owner",
            CreatedAt = Origin,
            UpdatedAt = Origin,
        });
        context.SaveChanges();
    }

    private void DeleteConversation(string id)
    {
        using var context = _contextFactory.CreateDbContext();
        context.Conversations.Where(c => c.Id == id).ExecuteDelete();
    }

    private List<string> BudgetKeys()
    {
        using var context = _contextFactory.CreateDbContext();
        return [.. context.ConversationBudgets.Select(b => b.BudgetKey)];
    }

    /// <summary>Releases the temporary database directory.</summary>
    public void Dispose()
    {
        // Pooled connections keep a handle on the file, so the directory delete below fails without
        // this — the same reason the sibling store tests clear the pool.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
