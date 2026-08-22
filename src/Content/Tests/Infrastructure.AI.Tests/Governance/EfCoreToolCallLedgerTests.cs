using Application.AI.Common.Interfaces.Governance;
using FluentAssertions;
using Infrastructure.AI.Governance;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Governance;

/// <summary>
/// Tests for <see cref="EfCoreToolCallLedger"/>: a first claim succeeds, a second claim for the
/// same conversation/tool pair is refused, different conversations and different tools each get
/// their own claim, and — the property the whole design exists to guarantee — a concurrent burst
/// of claims for the same pair admits exactly one.
/// </summary>
public sealed class EfCoreToolCallLedgerTests : IDisposable
{
    private const string ConversationA = "conv-a";
    private const string ConversationB = "conv-b";
    private const string Tool = "start_diagnostic_session";

    private readonly string _dbPath;
    private readonly DbContextOptions<GovernanceStateDbContext> _options;

    public EfCoreToolCallLedgerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tool-call-ledger-test-{Guid.NewGuid():N}.db");
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

    private IToolCallLedger CreateLedger()
    {
        var factory = new TestContextFactory(_options);
        return new EfCoreToolCallLedger(
            factory,
            new SchemaInitializer<GovernanceStateDbContext>(factory),
            TimeProvider.System,
            NullLogger<EfCoreToolCallLedger>.Instance);
    }

    [Fact]
    public async Task TryClaimAsync_FirstClaim_Succeeds()
    {
        var ledger = CreateLedger();

        var claimed = await ledger.TryClaimAsync(ConversationA, Tool, CancellationToken.None);

        claimed.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaimAsync_SecondClaimSameConversationAndTool_Refuses()
    {
        var ledger = CreateLedger();
        await ledger.TryClaimAsync(ConversationA, Tool, CancellationToken.None);

        var second = await ledger.TryClaimAsync(ConversationA, Tool, CancellationToken.None);

        second.Should().BeFalse();
    }

    [Fact]
    public async Task TryClaimAsync_SecondClaimAgainstANewLedgerInstance_StillRefuses()
    {
        // Proves durability, not just an in-process guard: a fresh EfCoreToolCallLedger over the
        // same database file is what a second bundle run gets, in a possibly different process.
        var first = CreateLedger();
        await first.TryClaimAsync(ConversationA, Tool, CancellationToken.None);

        var second = CreateLedger();
        var claimed = await second.TryClaimAsync(ConversationA, Tool, CancellationToken.None);

        claimed.Should().BeFalse();
    }

    [Fact]
    public async Task TryClaimAsync_DifferentConversation_ClaimsIndependently()
    {
        var ledger = CreateLedger();
        await ledger.TryClaimAsync(ConversationA, Tool, CancellationToken.None);

        var claimedInB = await ledger.TryClaimAsync(ConversationB, Tool, CancellationToken.None);

        claimedInB.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaimAsync_DifferentTool_ClaimsIndependently()
    {
        var ledger = CreateLedger();
        await ledger.TryClaimAsync(ConversationA, Tool, CancellationToken.None);

        var claimedOtherTool = await ledger.TryClaimAsync(ConversationA, "other_tool", CancellationToken.None);

        claimedOtherTool.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaimAsync_ConcurrentBurstForTheSamePair_AdmitsExactlyOne()
    {
        // The property this design exists to guarantee — see IToolCallLedger's remarks on why a
        // read-then-write here would admit an entire parallel batch of the same tool call. Mirrors
        // ProgressEvaluatorConcurrencyTests' role as the control for the loop guard's identical
        // atomicity requirement.
        var ledger = CreateLedger();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => ledger.TryClaimAsync(ConversationA, Tool, CancellationToken.None)));

        results.Count(claimed => claimed).Should().Be(1);
    }

    private sealed class TestContextFactory(DbContextOptions<GovernanceStateDbContext> options)
        : IDbContextFactory<GovernanceStateDbContext>
    {
        public GovernanceStateDbContext CreateDbContext() => new(options);
    }
}
