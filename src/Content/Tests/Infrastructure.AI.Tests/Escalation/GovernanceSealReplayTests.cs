using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Changes;
using Domain.AI.Escalation;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Tests.Changes.Support;
using Infrastructure.AI.Tests.Escalation.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Regression tests for seal replay across records, and for the reclaim of an abandoned
/// reconcile claim.
/// </summary>
/// <remarks>
/// The replay case is the one that makes the seal worth having. Sealing the payload alone
/// catches a <em>modified</em> verdict but not a <em>relocated</em> one: an approved outcome
/// copied verbatim into another escalation's row verifies byte-for-byte, and the plan
/// executor's resume path branches on <c>IsApproved</c> without re-checking the id — so one
/// genuine approval would approve every escalation after it.
/// </remarks>
public sealed class GovernanceSealReplayTests : IDisposable
{
    private readonly FakeGovernanceRecordSealer _sealer = new();
    private readonly string _dbPath;
    private readonly DbContextOptions<GovernanceStateDbContext> _options;

    public GovernanceSealReplayTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gov-replay-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<GovernanceStateDbContext>()
            .UseSqlite($"DataSource={_dbPath}")
            .Options;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private EfCoreEscalationStateStore CreateEscalationStore()
    {
        var factory = new TestContextFactory(_options);
        return new EfCoreEscalationStateStore(
            factory,
            new SchemaInitializer<GovernanceStateDbContext>(factory),
            _sealer,
            GovernanceStateTestConfig.Monitor(),
            NullLogger<EfCoreEscalationStateStore>.Instance);
    }

    private EfCoreChangeProposalStore CreateProposalStore()
    {
        var factory = new TestContextFactory(_options);
        return new EfCoreChangeProposalStore(
            factory,
            new SchemaInitializer<GovernanceStateDbContext>(factory),
            _sealer,
            GovernanceStateTestConfig.Monitor(),
            NullLogger<EfCoreChangeProposalStore>.Instance);
    }

    private static EscalationRequest CreateRequest(Guid id) => new()
    {
        EscalationId = id,
        AgentId = "agent-001",
        ToolName = "dangerous_tool",
        Arguments = new Dictionary<string, string>(),
        Description = "Test escalation",
        RiskLevel = RiskLevel.High,
        Priority = EscalationPriority.Blocking,
        Approvers = ["approver-1"],
        TimeoutSeconds = 300,
        RequestedAt = DateTimeOffset.UtcNow
    };

    private static EscalationOutcome ApprovedOutcome(Guid id) => new()
    {
        EscalationId = id,
        IsApproved = true,
        Decisions = [],
        ResolutionType = EscalationResolutionType.Approved,
        ResolvedAt = DateTimeOffset.UtcNow,
        Approvers = ["approver-1"]
    };

    /// <summary>
    /// Copies escalation A's outcome payload and seal verbatim onto escalation B's row and
    /// marks B resolved — the exact move a database writer would make to launder one real
    /// approval into an approval of everything.
    /// </summary>
    private async Task ReplayOutcomeAsync(Guid fromId, Guid toId)
    {
        await using var context = new GovernanceStateDbContext(_options);
        var source = await context.Escalations.SingleAsync(e => e.Id == fromId);
        var target = await context.Escalations.SingleAsync(e => e.Id == toId);

        target.OutcomeJson = source.OutcomeJson;
        target.OutcomeSealJson = source.OutcomeSealJson;
        target.Status = nameof(EscalationPersistedStatus.Resolved);
        await context.SaveChangesAsync();
    }

    // ===== HIGH: seal replay across escalations =====

    [Fact]
    public async Task GetResolvedOutcomeAsync_OutcomeAndSealReplayedFromAnotherEscalation_ReturnsNull()
    {
        var approvedId = Guid.NewGuid();
        var victimId = Guid.NewGuid();
        var store = CreateEscalationStore();

        await store.SavePendingAsync(CreateRequest(approvedId), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.SavePendingAsync(CreateRequest(victimId), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.MarkResolvedPendingAuditAsync(ApprovedOutcome(approvedId), CancellationToken.None);
        await store.MarkResolvedAsync(approvedId, CancellationToken.None);

        await ReplayOutcomeAsync(approvedId, victimId);

        // The genuine record still verifies...
        (await store.GetResolvedOutcomeAsync(approvedId, CancellationToken.None))
            .Should().NotBeNull();

        // ...but the relocated copy must not, even though every byte of payload and seal is
        // untouched. Returning it here would release the victim's human gate.
        (await store.GetResolvedOutcomeAsync(victimId, CancellationToken.None))
            .Should().BeNull("a verdict lifted from another escalation must never be served");
    }

    [Fact]
    public async Task GetActiveAsync_ParkedOutcomeReplayedFromAnotherEscalation_WithholdsTheOutcome()
    {
        var approvedId = Guid.NewGuid();
        var victimId = Guid.NewGuid();
        var store = CreateEscalationStore();

        await store.SavePendingAsync(CreateRequest(approvedId), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.SavePendingAsync(CreateRequest(victimId), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.MarkResolvedPendingAuditAsync(ApprovedOutcome(approvedId), CancellationToken.None);

        // Park the victim with the approved escalation's payload + seal.
        await using (var context = new GovernanceStateDbContext(_options))
        {
            var source = await context.Escalations.SingleAsync(e => e.Id == approvedId);
            var target = await context.Escalations.SingleAsync(e => e.Id == victimId);
            target.OutcomeJson = source.OutcomeJson;
            target.OutcomeSealJson = source.OutcomeSealJson;
            target.Status = nameof(EscalationPersistedStatus.ResolvedPendingAudit);
            await context.SaveChangesAsync();
        }

        var active = await store.GetActiveAsync(CancellationToken.None);
        var victim = active.Single(s => s.Request.EscalationId == victimId);

        // A null Outcome is what makes the reconciler skip it — a relocated verdict must never
        // be re-driven into the hash-chained compliance log under the victim's id.
        victim.Outcome.Should().BeNull();
    }

    // ===== MED: change proposals are sealed too =====

    [Fact]
    public async Task GetAsync_ProposalStatusEditedInTheDatabase_RefusesToServeIt()
    {
        var store = CreateProposalStore();
        var proposal = TestProposals.NewProposal();
        await store.SaveAsync(proposal, CancellationToken.None);

        // A database writer promotes the proposal to an approved state without re-sealing.
        await using (var context = new GovernanceStateDbContext(_options))
        {
            var row = await context.ChangeProposals.SingleAsync(p => p.Id == proposal.Id);
            row.ProposalJson = row.ProposalJson.Replace("\"Draft\"", "\"Approved\"", StringComparison.Ordinal);
            row.Status = nameof(ChangeProposalStatus.Approved);
            await context.SaveChangesAsync();
        }

        (await store.GetAsync(proposal.Id, CancellationToken.None)).Should().BeNull();
        (await store.ListAsync(new ChangeProposalQuery(), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_ProposalSealReplayedFromAnotherProposal_RefusesToServeIt()
    {
        var store = CreateProposalStore();
        var original = TestProposals.NewProposal();
        var other = original with { Id = original.Id + "-other" };
        await store.SaveAsync(original, CancellationToken.None);
        await store.SaveAsync(other, CancellationToken.None);

        await using (var context = new GovernanceStateDbContext(_options))
        {
            var source = await context.ChangeProposals.SingleAsync(p => p.Id == original.Id);
            var target = await context.ChangeProposals.SingleAsync(p => p.Id == other.Id);
            target.ProposalJson = source.ProposalJson;
            target.ProposalSealJson = source.ProposalSealJson;
            await context.SaveChangesAsync();
        }

        (await store.GetAsync(other.Id, CancellationToken.None))
            .Should().BeNull("a proposal payload lifted onto another row must not verify");
    }

    // ===== HIGH: an abandoned AuditInFlight claim is reclaimable =====

    [Fact]
    public async Task TryClaimResolvedPendingAuditAsync_StaleAuditInFlightRow_IsReclaimed()
    {
        var id = Guid.NewGuid();
        var store = CreateEscalationStore();
        await store.SavePendingAsync(CreateRequest(id), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.MarkResolvedPendingAuditAsync(ApprovedOutcome(id), CancellationToken.None);

        // First pass claims it, then dies (kill -9 / OOM / eviction) without releasing.
        (await store.TryClaimResolvedPendingAuditAsync(id, DateTimeOffset.UtcNow.AddMinutes(-10), CancellationToken.None))
            .Should().BeTrue();

        // A pass running immediately afterwards must NOT steal a live claim.
        (await store.TryClaimResolvedPendingAuditAsync(id, DateTimeOffset.UtcNow.AddMinutes(-10), CancellationToken.None))
            .Should().BeFalse("a claim that is merely in progress must not be stolen");

        // Once the claim ages past the staleness bound it becomes reclaimable — otherwise the
        // row would sit in AuditInFlight forever, skipped by every pass and left by the pruner.
        (await store.TryClaimResolvedPendingAuditAsync(id, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None))
            .Should().BeTrue("an abandoned claim must be reclaimable, not terminally stuck");
    }

    [Fact]
    public async Task GetActiveAsync_AuditInFlightRow_IsStillReturnedForReconciliation()
    {
        var id = Guid.NewGuid();
        var store = CreateEscalationStore();
        await store.SavePendingAsync(CreateRequest(id), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.MarkResolvedPendingAuditAsync(ApprovedOutcome(id), CancellationToken.None);
        await store.TryClaimResolvedPendingAuditAsync(id, DateTimeOffset.UtcNow.AddMinutes(-10), CancellationToken.None);

        var active = await store.GetActiveAsync(CancellationToken.None);

        var snapshot = active.Should().ContainSingle().Subject;
        snapshot.Status.Should().Be(EscalationPersistedStatus.AuditInFlight);
        snapshot.Outcome.Should().NotBeNull("the reconciler needs the verdict to re-drive it");
    }

    /// <summary>Minimal context factory over fixed options.</summary>
    private sealed class TestContextFactory(DbContextOptions<GovernanceStateDbContext> options)
        : IDbContextFactory<GovernanceStateDbContext>
    {
        public GovernanceStateDbContext CreateDbContext() => new(options);
    }
}
