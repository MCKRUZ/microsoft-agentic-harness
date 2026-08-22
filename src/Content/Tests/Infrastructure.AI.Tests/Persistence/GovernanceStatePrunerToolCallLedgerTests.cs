using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Persistence;

/// <summary>
/// Tests that <see cref="GovernanceStatePruner"/> prunes <c>tool_call_ledger</c> rows by age
/// alone, with no status filter — a ledger row has no lifecycle to be mid-way through.
/// </summary>
public sealed class GovernanceStatePrunerToolCallLedgerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<GovernanceStateDbContext> _options;

    public GovernanceStatePrunerToolCallLedgerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gov-pruner-ledger-test-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<GovernanceStateDbContext>()
            .UseSqlite($"DataSource={_dbPath}")
            .Options;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; the OS temp folder reaps leftovers.
        }
    }

    [Fact]
    public async Task PruneAsync_LedgerRowOlderThanCutoff_IsRemoved_NewerRowSurvives()
    {
        var factory = new TestContextFactory(_options);
        var pruner = new GovernanceStatePruner(
            factory, new SchemaInitializer<GovernanceStateDbContext>(factory), NullLogger<GovernanceStatePruner>.Instance);

        var now = DateTimeOffset.UtcNow;
        await using (var context = factory.CreateDbContext())
        {
            context.ToolCallLedger.Add(new ToolCallLedgerEntity
            {
                ConversationId = "old-conv",
                ToolName = "start_diagnostic_session",
                CalledAtTicks = now.AddDays(-100).UtcTicks
            });
            context.ToolCallLedger.Add(new ToolCallLedgerEntity
            {
                ConversationId = "recent-conv",
                ToolName = "start_diagnostic_session",
                CalledAtTicks = now.AddDays(-1).UtcTicks
            });
            await context.SaveChangesAsync();
        }

        var result = await pruner.PruneAsync(now.AddDays(-90), CancellationToken.None);

        result.ToolCallLedgerRowsRemoved.Should().Be(1);
        await using var verify = factory.CreateDbContext();
        verify.ToolCallLedger.Should().ContainSingle().Which.ConversationId.Should().Be("recent-conv");
    }

    private sealed class TestContextFactory(DbContextOptions<GovernanceStateDbContext> options)
        : IDbContextFactory<GovernanceStateDbContext>
    {
        public GovernanceStateDbContext CreateDbContext() => new(options);
    }
}
