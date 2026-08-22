using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Persistence;

/// <summary>
/// Tests that <see cref="GovernanceStatePruner"/> never deletes <c>tool_call_ledger</c> rows by
/// age — a ledger row is the enforcement token itself, not an audit record, so pruning it by age
/// alone would re-arm a call-once tool for any conversation still live past the retention window.
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
    public async Task PruneAsync_LedgerRowsOfAnyAge_AreNeverRemoved()
    {
        // Mutation control for the fix this test exists to lock in: an earlier version of the
        // pruner aged tool_call_ledger rows out by CalledAtTicks alone, which is exactly the
        // defect this asserts against — a row far older than the retention cutoff must still
        // survive a prune pass, because there is no way for this pruner to know whether the
        // conversation or run it belongs to has actually ended.
        var factory = new TestContextFactory(_options);
        var pruner = new GovernanceStatePruner(
            factory, new SchemaInitializer<GovernanceStateDbContext>(factory), NullLogger<GovernanceStatePruner>.Instance);

        var now = DateTimeOffset.UtcNow;
        await using (var context = factory.CreateDbContext())
        {
            context.ToolCallLedger.Add(new ToolCallLedgerEntity
            {
                ConversationId = "very-old-conv",
                ToolName = "start_diagnostic_session",
                CalledAtTicks = now.AddDays(-3650).UtcTicks
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

        // The result shape itself has no ledger count — see IGovernanceStatePruner's remarks for
        // why the ledger is not part of this contract at all.
        result.EscalationsRemoved.Should().Be(0);
        result.ChangeProposalsRemoved.Should().Be(0);

        await using var verify = factory.CreateDbContext();
        verify.ToolCallLedger.Should().HaveCount(2,
            "neither row should be removed regardless of age — see GovernanceStatePruner's remarks");
    }

    private sealed class TestContextFactory(DbContextOptions<GovernanceStateDbContext> options)
        : IDbContextFactory<GovernanceStateDbContext>
    {
        public GovernanceStateDbContext CreateDbContext() => new(options);
    }
}
