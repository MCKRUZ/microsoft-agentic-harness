using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.Governance;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Governance;

/// <summary>
/// Right-to-erasure (#900) for <see cref="EfCoreToolCallLedger"/>: the call-once rows of the person's conversations are
/// removed, and every other row — another conversation's, a workflow run's — stays and keeps refusing a second call.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data and, for this store, re-arms a once-only tool somewhere it must stay
/// spent; under-deletion leaves the person's data behind while the erasure receipt claims success. Rows are keyed by
/// scope id only, so the subject's conversation ids are the whole match.
/// </remarks>
public sealed class EfCoreToolCallLedgerErasureTests : IDisposable
{
    private const string ToolA = "start_diagnostic_session";
    private const string ToolB = "send_report";

    private readonly string _dbPath;
    private readonly DbContextOptions<GovernanceStateDbContext> _options;
    private readonly EfCoreToolCallLedger _ledger;

    public EfCoreToolCallLedgerErasureTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tool-call-ledger-erase-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<GovernanceStateDbContext>()
            .UseSqlite($"DataSource={_dbPath}")
            .Options;
        var factory = new TestContextFactory(_options);
        _ledger = new EfCoreToolCallLedger(
            factory,
            new SchemaInitializer<GovernanceStateDbContext>(factory),
            TimeProvider.System,
            NullLogger<EfCoreToolCallLedger>.Instance);
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
    public async Task EraseSubjectAsync_SubjectsConversations_RemovesOnlyTheirRows()
    {
        await ClaimAsync("conv-a", ToolA);
        await ClaimAsync("conv-a", ToolB);
        await ClaimAsync("conv-b", ToolA);
        await ClaimAsync("conv-other", ToolA);
        await ClaimAsync("run-42", ToolA);
        (await ScopesAsync()).Should().HaveCount(5, "the seed must exist before erasing");

        var removed = await _ledger.EraseSubjectAsync(
            new ErasureSubject("user-a", ["conv-a", "conv-b"]), CancellationToken.None);

        removed.Should().Be(3);
        (await ScopesAsync()).Should().Equal(["conv-other", "run-42"]);
        (await _ledger.TryClaimAsync("conv-other", ToolA, CancellationToken.None))
            .Should().BeFalse("another conversation's once-only tool must stay spent");
        (await _ledger.TryClaimAsync("run-42", ToolA, CancellationToken.None))
            .Should().BeFalse("a workflow run's scope is not a conversation and must stay spent");
    }

    [Fact]
    public async Task EraseSubjectAsync_ScopeIdsDifferingOnlyInCase_AreNotErased()
    {
        await ClaimAsync("conv-a", ToolA);
        await ClaimAsync("CONV-A", ToolA);

        var removed = await _ledger.EraseSubjectAsync(new ErasureSubject("user-a", ["conv-a"]), CancellationToken.None);

        removed.Should().Be(1);
        (await ScopesAsync()).Should().Equal(["CONV-A"]);
    }

    [Fact]
    public async Task EraseSubjectAsync_NoConversations_ReturnsZeroAndChangesNothing()
    {
        await ClaimAsync("conv-other", ToolA);

        (await _ledger.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None)).Should().Be(0);
        (await _ledger.EraseSubjectAsync(new ErasureSubject("user-a", ["conv-gone"]), CancellationToken.None))
            .Should().Be(0);

        (await ScopesAsync()).Should().Equal(["conv-other"]);
    }

    private async Task ClaimAsync(string scopeId, string tool) =>
        (await _ledger.TryClaimAsync(scopeId, tool, CancellationToken.None)).Should().BeTrue();

    private async Task<List<string>> ScopesAsync()
    {
        await using var context = new GovernanceStateDbContext(_options);
        return await context.ToolCallLedger.Select(e => e.ConversationId).OrderBy(id => id).ToListAsync();
    }

    private sealed class TestContextFactory(DbContextOptions<GovernanceStateDbContext> options)
        : IDbContextFactory<GovernanceStateDbContext>
    {
        public GovernanceStateDbContext CreateDbContext() => new(options);
    }
}
