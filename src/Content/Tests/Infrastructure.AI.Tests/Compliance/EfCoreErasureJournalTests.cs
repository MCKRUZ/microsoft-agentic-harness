using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.Compliance;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Compliance;

/// <summary>
/// <see cref="EfCoreErasureJournal"/> (#907). The journal is the only way back to what a failed erasure request's deleted
/// conversations and plans led to: losing a row strands records, and keeping one past its time keeps a person's ids.
/// </summary>
public sealed class EfCoreErasureJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"erasure-journal-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly EfCoreErasureJournal _journal;

    public EfCoreErasureJournalTests()
    {
        Directory.CreateDirectory(_dir);
        var factory = new Factory(new DbContextOptionsBuilder<ErasureJournalDbContext>()
            .UseSqlite($"DataSource={Path.Combine(_dir, "journal.db")};Pooling=False")
            .Options);
        _journal = new EfCoreErasureJournal(factory, new SchemaInitializer<ErasureJournalDbContext>(factory), _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static ErasureJournalEntries Entries(string tag) => new(
        [$"conv-{tag}"],
        [$"run:{tag}"],
        new PlanRunTraces([$"plan-{tag}:step"], [$"job-{tag}"], [$"planrun:plan-{tag}"]));

    [Fact]
    public async Task RecordAsync_ThenReadAsync_ReturnsEveryKindOfId_ForThatOwnerOnly()
    {
        await _journal.RecordAsync("user-a", "req-1", Entries("a"), CancellationToken.None);
        await _journal.RecordAsync("user-b", "req-2", Entries("b"), CancellationToken.None);

        var read = await _journal.ReadAsync("user-a", CancellationToken.None);

        read.ConversationIds.Should().Equal("conv-a");
        read.WithheldConversationIds.Should().Equal("run:a");
        read.PlanRuns.StepConversationIds.Should().Equal("plan-a:step");
        read.PlanRuns.ToolResultScopeIds.Should().Equal("job-a");
        read.PlanRuns.RunBudgetKeys.Should().Equal("planrun:plan-a");
    }

    [Fact]
    public async Task ReadAsync_MatchesTheOwnerExactlyAsIssued()
    {
        await _journal.RecordAsync("User-A", "req-1", Entries("a"), CancellationToken.None);

        (await _journal.ReadAsync("user-a", CancellationToken.None)).IsEmpty.Should().BeTrue();
        (await _journal.ReadAsync("User-A", CancellationToken.None)).IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAsync_RemovesThatRequestsRows_ButKeepsWithheldIds()
    {
        await _journal.RecordAsync("user-a", "req-1", Entries("a"), CancellationToken.None);

        await _journal.ClearAsync("user-a", "req-1", CancellationToken.None);

        var read = await _journal.ReadAsync("user-a", CancellationToken.None);
        read.ConversationIds.Should().BeEmpty();
        read.PlanRuns.IsEmpty.Should().BeTrue();
        read.WithheldConversationIds.Should().Equal(["run:a"], "withheld ids are never erased through, so they age out instead");
    }

    [Fact]
    public async Task ClearAsync_KeepsRowsAConcurrentRequestTookOver()
    {
        // Two erasures for one person can overlap: the first must not clear what the second just recorded and still needs.
        await _journal.RecordAsync("user-a", "req-1", Entries("a"), CancellationToken.None);
        await _journal.RecordAsync("user-a", "req-2", Entries("a"), CancellationToken.None);

        await _journal.ClearAsync("user-a", "req-1", CancellationToken.None);

        (await _journal.ReadAsync("user-a", CancellationToken.None)).ConversationIds.Should().Equal("conv-a");
    }

    [Fact]
    public async Task ClearAsync_OnlyTouchesItsOwnOwner()
    {
        await _journal.RecordAsync("user-a", "req-1", Entries("a"), CancellationToken.None);
        await _journal.RecordAsync("user-b", "req-1", Entries("b"), CancellationToken.None);

        await _journal.ClearAsync("user-a", "req-1", CancellationToken.None);

        (await _journal.ReadAsync("user-b", CancellationToken.None)).ConversationIds.Should().Equal("conv-b");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_RemovesOnlyRowsLastRecordedBeforeTheCutoff()
    {
        await _journal.RecordAsync("user-a", "req-1", Entries("old"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(10));
        await _journal.RecordAsync("user-a", "req-2", Entries("new"), CancellationToken.None);

        var removed = await _journal.PurgeOlderThanAsync(TimeSpan.FromDays(5), CancellationToken.None);

        removed.Should().Be(5, "every kind of id the old request recorded");
        var read = await _journal.ReadAsync("user-a", CancellationToken.None);
        read.ConversationIds.Should().Equal("conv-new");
        read.WithheldConversationIds.Should().Equal("run:new");
    }

    [Fact]
    public async Task HoldsForAnotherOwnerAsync_HoldsConversationAndWithheldIds_FromEveryoneButTheirOwner()
    {
        await _journal.RecordAsync("user-a", "req-1", Entries("a"), CancellationToken.None);

        (await _journal.HoldsForAnotherOwnerAsync("conv-a", "user-b", CancellationToken.None)).Should().BeTrue();
        (await _journal.HoldsForAnotherOwnerAsync("run:a", "user-b", CancellationToken.None)).Should().BeTrue();
        (await _journal.HoldsForAnotherOwnerAsync("conv-a", "user-a", CancellationToken.None)).Should().BeFalse(
            "the person may reuse their own id; the records under it are theirs either way");
        (await _journal.HoldsForAnotherOwnerAsync("job-a", "user-b", CancellationToken.None)).Should().BeFalse(
            "only conversation ids name a conversation someone could create");
        (await _journal.HoldsForAnotherOwnerAsync("conv-z", "user-b", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_AWithheldIdRecordedAgain_KeepsItsFirstTime_SoItStillAgesOut()
    {
        // Withheld ids are only removed by age; a person re-running erasure must not keep them forever.
        await _journal.RecordAsync("user-a", "req-1", Entries("a"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(10));
        await _journal.RecordAsync("user-a", "req-2", Entries("a"), CancellationToken.None);

        (await _journal.PurgeOlderThanAsync(TimeSpan.FromDays(5), CancellationToken.None)).Should().Be(1);
        (await _journal.ReadAsync("user-a", CancellationToken.None)).WithheldConversationIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordAsync_AnIdRecordedAgain_RefreshesItsTime()
    {
        var unfinished = Entries("a") with { WithheldConversationIds = [] };
        await _journal.RecordAsync("user-a", "req-1", unfinished, CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(10));
        await _journal.RecordAsync("user-a", "req-2", unfinished, CancellationToken.None);

        (await _journal.PurgeOlderThanAsync(TimeSpan.FromDays(5), CancellationToken.None)).Should().Be(0);
    }

    private sealed class Factory(DbContextOptions<ErasureJournalDbContext> options) : IDbContextFactory<ErasureJournalDbContext>
    {
        public ErasureJournalDbContext CreateDbContext() => new(options);
    }
}
