using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// <see cref="SqliteConversationBudgetTracker"/> against the shared budget contract, plus everything
/// that only exists because this tracker is durable: two hosts sharing one total, a total that outlives
/// the process, and the hand-written upsert agreeing with the entity model.
/// </summary>
/// <remarks>
/// <para>
/// Runs against a real SQLite <em>file</em>, for the same reason the store's and the lease's tests do:
/// a <c>:memory:</c> database lives inside one connection, which would make every "second host" in
/// these tests the same host and prove nothing.
/// </para>
/// <para>
/// A second tracker instance over the same file is what "another host" means here. It shares no
/// dictionary, no cache and no clock with the first — only the database — which is exactly the
/// condition the in-process implementation fails and this one must not.
/// </para>
/// </remarks>
public sealed class SqliteConversationBudgetTrackerTests
    : ConversationBudgetTrackerContractTests, IDisposable
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly TestConversationDbContextFactory _contextFactory;
    private readonly SqliteConversationBudgetTracker _tracker;
    private readonly SqliteConversationBudgetTracker _disabled;

    /// <summary>Creates an isolated on-disk database and the trackers under test.</summary>
    public SqliteConversationBudgetTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convbudget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _databasePath = Path.Combine(_tempDir, "conversations.db");
        _contextFactory = new TestConversationDbContextFactory(_databasePath);

        _tracker = Build(_contextFactory, Ceiling);
        _disabled = Build(_contextFactory, 0);
    }

    /// <inheritdoc />
    protected override IConversationBudgetTracker Tracker => _tracker;

    /// <inheritdoc />
    protected override IConversationBudgetTracker DisabledTracker => _disabled;

    private static SqliteConversationBudgetTracker Build(
        IDbContextFactory<ConversationDbContext> contextFactory,
        int ceiling)
    {
        var config = new AppConfig();
        config.AI.AgentFramework.ConversationTokenBudget = ceiling;

        return new SqliteConversationBudgetTracker(
            contextFactory,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            new FakeTimeProvider(Origin),
            NullLogger<SqliteConversationBudgetTracker>.Instance,
            new SchemaInitializer<ConversationDbContext>(contextFactory));
    }

    /// <summary>
    /// The reason this implementation exists. Two hosts running turns of one conversation must enforce
    /// one ceiling between them; with a per-process total they each enforce a private copy and the
    /// conversation spends roughly twice what was configured.
    /// </summary>
    [Fact]
    public async Task TwoHostsSharingTheDatabase_ShareOneTotal()
    {
        var second = Build(_contextFactory, Ceiling);
        var key = NewKey();

        await _tracker.RecordUsageAsync(key, 600);
        await second.RecordUsageAsync(key, 400);

        // Each host must see the sum, not just its own half.
        (await _tracker.GetStatusAsync(key)).ConsumedTokens.Should().Be(1_000);
        (await second.GetStatusAsync(key)).ConsumedTokens.Should().Be(1_000);
        (await second.GetStatusAsync(key)).IsExhausted.Should()
            .BeTrue("600 spent elsewhere plus 400 spent here is the whole ceiling");
    }

    /// <summary>
    /// Concurrent accrual sums rather than colliding. The single-statement upsert is what makes this
    /// true — a read-then-write would have both hosts read the same total and one overwrite the other.
    /// </summary>
    [Fact]
    public async Task ConcurrentAccrualsFromManyInstances_AllLand()
    {
        var key = NewKey();
        const int instances = 8;
        const int perInstance = 10;

        var trackers = Enumerable.Range(0, instances)
            .Select(_ => Build(_contextFactory, Ceiling))
            .ToList();

        await Task.WhenAll(trackers.Select(async t =>
        {
            for (var i = 0; i < perInstance; i++)
                await t.RecordUsageAsync(key, 1);
        }));

        (await _tracker.GetStatusAsync(key)).ConsumedTokens.Should()
            .Be(instances * perInstance, "every accrual must be added, not overwritten");
    }

    /// <summary>
    /// A total survives every object that recorded it being discarded — the process-restart case, and
    /// the difference between a budget that spans a conversation and one that spans an uptime.
    /// </summary>
    [Fact]
    public async Task TotalSurvivesTheTrackerAndItsContextFactory()
    {
        var key = NewKey();
        await _tracker.RecordUsageAsync(key, 750);

        // A brand-new factory over the same path: nothing in memory carries over.
        var reopened = new TestConversationDbContextFactory(_databasePath);
        var afterRestart = Build(reopened, Ceiling);

        (await afterRestart.GetStatusAsync(key)).ConsumedTokens.Should().Be(750);
    }

    /// <summary>
    /// The accrual statement is hand-written SQL because EF cannot express <c>ON CONFLICT DO UPDATE</c>,
    /// which repeats the table and column names outside the model. This fails if the two drift — a
    /// renamed property, a <c>HasColumnName</c>, a changed table — which would otherwise surface as a
    /// budget that silently never accrues.
    /// </summary>
    [Fact]
    public async Task Accrual_WritesRowsTheEntityModelCanRead()
    {
        var key = NewKey();
        await _tracker.RecordUsageAsync(key, 123);

        await using var context = _contextFactory.CreateDbContext();
        var row = await context.ConversationBudgets.SingleAsync(e => e.BudgetKey == key);

        row.ConsumedTokens.Should().Be(123);
        row.UpdatedAt.Should().Be(Origin, "the timestamp must round-trip through the ticks converter");
    }

    /// <summary>
    /// A disabled ceiling must not touch the database at all, so the default deployment pays nothing
    /// for a feature it has not turned on.
    /// </summary>
    [Fact]
    public async Task DisabledCeiling_WritesNoRow()
    {
        var key = NewKey();
        await _disabled.RecordUsageAsync(key, 500);

        await using var context = _contextFactory.CreateDbContext();
        (await context.ConversationBudgets.AnyAsync(e => e.BudgetKey == key)).Should().BeFalse();
    }

    /// <summary>
    /// A budget key is not a conversation id and must not require one. Two of the four callers have no
    /// conversation row — a plan run's namespaced key, and the command handler's own id — so a foreign
    /// key or an existence check here would break exactly the callers this table exists to serve.
    /// </summary>
    [Fact]
    public async Task AccruesAgainstAKeyWithNoConversationRow()
    {
        const string planRunKey = "planrun:no-such-conversation";

        await _tracker.RecordUsageAsync(planRunKey, 400);

        (await _tracker.GetStatusAsync(planRunKey)).ConsumedTokens.Should().Be(400);
    }

    /// <summary>Releases the temporary database file.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open by a connection the runtime has not finalised is not a test
            // failure; the temp directory is disposable either way.
        }
    }
}
