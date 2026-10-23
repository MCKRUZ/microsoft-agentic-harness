using FluentAssertions;
using Infrastructure.AI.Documents;
using Infrastructure.AI.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Documents;

/// <summary>
/// The document load record (#901) against real SQLite: a load time only ever moves forward, the age limit's scan is
/// oldest first and strict, and forgetting a document leaves it alone if it was loaded again meanwhile.
/// </summary>
public sealed class EfCoreDocumentLoadLedgerTests : IDisposable
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _keepAlive;
    private readonly Factory _factory;

    public EfCoreDocumentLoadLedgerTests()
    {
        var connectionString = $"Data Source=file:documentloads-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        _factory = new Factory(new DbContextOptionsBuilder<DocumentLoadDbContext>().UseSqlite(connectionString).Options);
    }

    /// <inheritdoc />
    public void Dispose() => _keepAlive.Dispose();

    private EfCoreDocumentLoadLedger Ledger(DateTimeOffset now) =>
        new(_factory, new SchemaInitializer<DocumentLoadDbContext>(_factory), new FakeTimeProvider(now));

    [Fact]
    public async Task RecordLoaded_StampsTheClockAndALaterLoadMovesItForward()
    {
        await Ledger(Origin).RecordLoadedAsync("A", CancellationToken.None);
        (await Ledger(Origin).GetLastLoadedAsync("A", CancellationToken.None)).Should().Be(Origin);

        await Ledger(Origin.AddDays(5)).RecordLoadedAsync("A", CancellationToken.None);

        (await Ledger(Origin).GetLastLoadedAsync("A", CancellationToken.None)).Should().Be(Origin.AddDays(5));
    }

    [Fact]
    public async Task RecordLoaded_AnEarlierClockNeverMovesTheTimeBack()
    {
        // Two hosts with skewed clocks: the later load must win, or a document just loaded could look old.
        await Ledger(Origin.AddDays(5)).RecordLoadedAsync("A", CancellationToken.None);
        await Ledger(Origin).RecordLoadedAsync("A", CancellationToken.None);

        (await Ledger(Origin).GetLastLoadedAsync("A", CancellationToken.None)).Should().Be(Origin.AddDays(5));
    }

    [Fact]
    public async Task RecordLoaded_TheSameDocumentAtOnce_AllSucceedWithOneRecord()
    {
        var ledger = Ledger(Origin);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ledger.RecordLoadedAsync("A", CancellationToken.None)));

        await using var ctx = _factory.CreateDbContext();
        (await ctx.DocumentLoads.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ListLoadedBefore_IsOldestFirst_Strict_AndBounded()
    {
        await Ledger(Origin.AddDays(2)).RecordLoadedAsync("newer", CancellationToken.None);
        await Ledger(Origin).RecordLoadedAsync("oldest", CancellationToken.None);
        await Ledger(Origin.AddDays(1)).RecordLoadedAsync("middle", CancellationToken.None);
        await Ledger(Origin.AddDays(3)).RecordLoadedAsync("at-cutoff", CancellationToken.None);
        var ledger = Ledger(Origin);

        (await ledger.ListLoadedBeforeAsync(Origin.AddDays(3), 10, CancellationToken.None))
            .Should().Equal(["oldest", "middle", "newer"], "a document loaded exactly at the cutoff is not yet past it");
        (await ledger.ListLoadedBeforeAsync(Origin.AddDays(3), 2, CancellationToken.None))
            .Should().Equal(["oldest", "middle"]);
    }

    [Fact]
    public async Task Forget_RemovesTheRecord_UnlessTheDocumentWasLoadedAgainSinceTheRemovalStarted()
    {
        await Ledger(Origin).RecordLoadedAsync("removed", CancellationToken.None);
        await Ledger(Origin.AddHours(1)).RecordLoadedAsync("reloaded", CancellationToken.None);
        var ledger = Ledger(Origin);

        (await ledger.ForgetAsync("removed", Origin, CancellationToken.None)).Should().BeTrue();
        (await ledger.ForgetAsync("reloaded", Origin, CancellationToken.None))
            .Should().BeFalse("a document loaded while it was being removed keeps its load time");

        (await ledger.GetLastLoadedAsync("removed", CancellationToken.None)).Should().BeNull();
        (await ledger.GetLastLoadedAsync("reloaded", CancellationToken.None)).Should().Be(Origin.AddHours(1));
    }

    private sealed class Factory(DbContextOptions<DocumentLoadDbContext> options) : IDbContextFactory<DocumentLoadDbContext>
    {
        public DocumentLoadDbContext CreateDbContext() => new(options);
    }
}
