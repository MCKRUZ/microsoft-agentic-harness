using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Prompts.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

/// <summary>
/// Right-to-erasure (#900) for <see cref="EfCorePromptUsageStore"/>: a row is the person's only when its case id is
/// exactly one of their conversation ids, a colon and a turn number, and only those rows are removed.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. The rows carry no owner, only a <c>{conversationId}:{turn}</c> case id, so the matching rule
/// is the whole guarantee: a prefix match would also take <c>conv1:3</c> or <c>conv-x:1</c> when erasing <c>conv</c>.
/// </remarks>
public sealed class EfCorePromptUsageStoreErasureTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly IDbContextFactory<PromptUsageDbContext> _factory;
    private readonly EfCorePromptUsageStore _sut;

    public EfCorePromptUsageStoreErasureTests()
    {
        var connectionString = $"Data Source=file:promptusage-erase-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        _factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<PromptUsageDbContext>().UseSqlite(connectionString).Options);
        using (var ctx = _factory.CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        _sut = new EfCorePromptUsageStore(_factory, new SchemaInitializer<PromptUsageDbContext>(_factory));
    }

    public void Dispose() => _keepAlive.Dispose();

    [Fact]
    public async Task EraseSubjectAsync_FreshDatabaseNothingRecordedYet_ErasesNothingWithoutFailing()
    {
        // The schema used to be created only when a usage recorder was first resolved, so erasing someone before anything
        // was recorded failed with "no such table", and a failed store holds the person's conversations back.
        var connectionString = $"Data Source=file:promptusage-fresh-{Guid.NewGuid():N}?mode=memory&cache=shared";
        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<PromptUsageDbContext>().UseSqlite(connectionString).Options);
        var store = new EfCorePromptUsageStore(factory, new SchemaInitializer<PromptUsageDbContext>(factory));

        var removed = await store.EraseSubjectAsync(new ErasureSubject("user-a", ["conv"]), CancellationToken.None);

        removed.Should().Be(0);
    }

    [Fact]
    public async Task EraseSubjectAsync_ConversationIdColonTurn_RemovesOnlyExactMatches()
    {
        string?[] caseIds =
        [
            "conv:7", "conv:12", "other:3",
            "conv1:3", "conv:extra:1", "conv-x:1", "conv:", "conv:7a", "conv", null,
        ];
        foreach (var caseId in caseIds)
            await _sut.AppendAsync(Record(caseId), CancellationToken.None);
        (await CaseIdsAsync()).Should().HaveCount(caseIds.Length, "the seed must exist before erasing");

        var removed = await _sut.EraseSubjectAsync(
            new ErasureSubject("user-a", ["conv", "other"]), CancellationToken.None);

        (await CaseIdsAsync()).Should().BeEquivalentTo(
            ["conv1:3", "conv:extra:1", "conv-x:1", "conv:", "conv:7a", "conv", null],
            "only '{conversationId}:{digits}' belongs to the conversation; every near miss is someone else's or not a turn");
        removed.Should().Be(3);
    }

    [Fact]
    public async Task EraseSubjectAsync_CaseIdDifferingOnlyInCase_IsNotErased()
    {
        // Conversation ids are compared exactly everywhere else; "CONV" is another conversation, not "conv".
        await _sut.AppendAsync(Record("conv:7"), CancellationToken.None);
        await _sut.AppendAsync(Record("CONV:7"), CancellationToken.None);

        var removed = await _sut.EraseSubjectAsync(new ErasureSubject("user-a", ["conv"]), CancellationToken.None);

        (await CaseIdsAsync()).Should().Equal(["CONV:7"], "another conversation's row must survive");
        removed.Should().Be(1);
    }

    [Fact]
    public async Task EraseSubjectAsync_ConversationIdWithLikeWildcards_MatchesLiterally()
    {
        // SQLite LIKE treats '_' and '%' as wildcards. A conversation id containing them must still match only itself.
        await _sut.AppendAsync(Record("c_nv:9"), CancellationToken.None);
        await _sut.AppendAsync(Record("conv:9"), CancellationToken.None);
        await _sut.AppendAsync(Record("c%:9"), CancellationToken.None);
        await _sut.AppendAsync(Record("cx:9"), CancellationToken.None);

        var removed = await _sut.EraseSubjectAsync(
            new ErasureSubject("user-a", ["c_nv", "c%"]), CancellationToken.None);

        (await CaseIdsAsync()).Should().BeEquivalentTo(["conv:9", "cx:9"]);
        removed.Should().Be(2);
    }

    [Fact]
    public async Task EraseSubjectAsync_NoMatchingRows_ReturnsZeroAndChangesNothing()
    {
        await _sut.AppendAsync(Record("other:1"), CancellationToken.None);

        (await _sut.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None)).Should().Be(0);
        (await _sut.EraseSubjectAsync(new ErasureSubject("user-a", ["conv"]), CancellationToken.None)).Should().Be(0);

        (await CaseIdsAsync()).Should().Equal(["other:1"]);
    }

    private async Task<List<string?>> CaseIdsAsync()
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.PromptUsages.Select(e => e.CaseId).ToListAsync();
    }

    private static PromptUsageRecord Record(string? caseId) => new()
    {
        Descriptor = new PromptDescriptor
        {
            Name = "p",
            Version = new PromptVersion(1, 0),
            ContentHash = "deadbeef",
            Body = "body",
        },
        CaseId = caseId,
        MetricKey = "m",
        TraceId = "trace-1",
        SpanId = "span-1",
        RecordedAtUtc = DateTimeOffset.UtcNow,
    };

    private sealed class TestDbContextFactory(DbContextOptions<PromptUsageDbContext> options)
        : IDbContextFactory<PromptUsageDbContext>
    {
        public PromptUsageDbContext CreateDbContext() => new(options);
    }
}
