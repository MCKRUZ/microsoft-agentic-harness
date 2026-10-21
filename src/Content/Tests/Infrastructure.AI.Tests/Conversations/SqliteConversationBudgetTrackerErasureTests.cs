using Domain.Common.Compliance;
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
/// Right-to-erasure (#900) for <see cref="SqliteConversationBudgetTracker"/>: the budget rows of the person's
/// conversations are removed, and every other row — another person's conversation, a plan run's key — keeps its total.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data and, here, also hands a live conversation a fresh ceiling; under-deletion
/// leaves the person's data behind while the erasure receipt claims success. Rows are keyed by conversation id only, so
/// the subject's conversation ids are the whole match. Against a real SQLite file, like the sweep tests.
/// </remarks>
public sealed class SqliteConversationBudgetTrackerErasureTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestConversationDbContextFactory _contextFactory;
    private readonly SqliteConversationBudgetTracker _tracker;

    /// <summary>Creates an isolated on-disk database and the tracker under test.</summary>
    public SqliteConversationBudgetTrackerErasureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convbudget-erase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _contextFactory = new TestConversationDbContextFactory(Path.Combine(_tempDir, "conversations.db"));

        var config = new AppConfig();
        config.AI.AgentFramework.ConversationTokenBudget = 10_000;

        _tracker = new SqliteConversationBudgetTracker(
            _contextFactory,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<SqliteConversationBudgetTracker>.Instance,
            new SchemaInitializer<ConversationDbContext>(_contextFactory));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task EraseSubjectAsync_SubjectsConversations_RemovesOnlyTheirRows()
    {
        await _tracker.RecordUsageAsync("conv-a", 100);
        await _tracker.RecordUsageAsync("conv-b", 200);
        await _tracker.RecordUsageAsync("conv-other", 300);
        await _tracker.RecordUsageAsync("planrun:run-1", 400);
        (await KeysAsync()).Should().HaveCount(4, "the seed must exist before erasing");

        var removed = await _tracker.EraseSubjectAsync(
            new ErasureSubject("user-a", ["conv-a", "conv-b", "conv-never-spent"]), CancellationToken.None);

        removed.Should().Be(2, "only rows that existed are counted");
        (await KeysAsync()).Should().Equal(["conv-other", "planrun:run-1"]);
        (await _tracker.GetStatusAsync("conv-other")).ConsumedTokens.Should().Be(300, "another conversation keeps its total");
        (await _tracker.GetStatusAsync("planrun:run-1")).ConsumedTokens.Should().Be(400, "a plan run's key is not a conversation");
    }

    [Fact]
    public async Task EraseSubjectAsync_ConversationIdsDifferingOnlyInCase_AreNotErased()
    {
        await _tracker.RecordUsageAsync("conv-a", 100);
        await _tracker.RecordUsageAsync("CONV-A", 200);

        var removed = await _tracker.EraseSubjectAsync(new ErasureSubject("user-a", ["conv-a"]), CancellationToken.None);

        removed.Should().Be(1);
        (await KeysAsync()).Should().Equal(["CONV-A"]);
    }

    [Fact]
    public async Task EraseSubjectAsync_NoConversations_ReturnsZeroAndChangesNothing()
    {
        await _tracker.RecordUsageAsync("conv-other", 300);

        (await _tracker.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None)).Should().Be(0);
        (await _tracker.EraseSubjectAsync(new ErasureSubject("user-a", ["conv-gone"]), CancellationToken.None))
            .Should().Be(0);

        (await KeysAsync()).Should().Equal(["conv-other"]);
    }

    private async Task<List<string>> KeysAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        return await context.ConversationBudgets.Select(e => e.BudgetKey).OrderBy(k => k).ToListAsync();
    }
}
