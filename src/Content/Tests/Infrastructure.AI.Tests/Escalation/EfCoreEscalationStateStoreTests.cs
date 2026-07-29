using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Tests.Escalation.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="EfCoreEscalationStateStore"/>: lifecycle transitions
/// (Pending → ResolvedPendingAudit → Resolved), the fail-closed visibility rule (an
/// un-audited outcome is never served), and durability across a new store instance over the
/// same database file.
/// </summary>
public sealed class EfCoreEscalationStateStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<GovernanceStateDbContext> _options;

    public EfCoreEscalationStateStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gov-state-test-{Guid.NewGuid():N}.db");
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

    private readonly FakeGovernanceRecordSealer _sealer = new();

    private EfCoreEscalationStateStore CreateStore(int maxPayloadBytes = 1024 * 1024)
    {
        var factory = new TestContextFactory(_options);
        return new EfCoreEscalationStateStore(
            factory,
            new GovernanceStateSchemaInitializer(factory),
            _sealer,
            GovernanceStateTestConfig.Monitor(maxPayloadBytes: maxPayloadBytes),
            NullLogger<EfCoreEscalationStateStore>.Instance);
    }

    private static EscalationRequest CreateRequest(Guid? id = null) => new()
    {
        EscalationId = id ?? Guid.NewGuid(),
        AgentId = "agent-001",
        ToolName = "dangerous_tool",
        Arguments = new Dictionary<string, string> { ["arg1"] = "value1" },
        Description = "Test escalation",
        RiskLevel = RiskLevel.High,
        Priority = EscalationPriority.Blocking,
        Approvers = ["approver-1", "approver-2"],
        TimeoutSeconds = 300,
        RequestedAt = DateTimeOffset.UtcNow
    };

    private static EscalationOutcome CreateOutcome(Guid escalationId, bool approved = true) => new()
    {
        EscalationId = escalationId,
        IsApproved = approved,
        Decisions =
        [
            new ApproverDecision
            {
                ApproverName = "approver-1",
                Approved = approved,
                Reason = "reviewed",
                RespondedAt = DateTimeOffset.UtcNow
            }
        ],
        ResolutionType = approved ? EscalationResolutionType.Approved : EscalationResolutionType.Denied,
        ResolvedAt = DateTimeOffset.UtcNow,
        Approvers = ["approver-1", "approver-2"]
    };

    [Fact]
    public async Task SavePending_GetActive_RoundTripsRequestAndCreatedAt()
    {
        var store = CreateStore();
        var request = CreateRequest();
        var createdAt = new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

        await store.SavePendingAsync(request, createdAt, CancellationToken.None);
        var active = await store.GetActiveAsync(CancellationToken.None);

        var snapshot = active.Should().ContainSingle().Subject;
        snapshot.Status.Should().Be(EscalationPersistedStatus.Pending);
        snapshot.CreatedAt.Should().Be(createdAt);
        snapshot.Decisions.Should().BeEmpty();
        snapshot.Outcome.Should().BeNull();
        snapshot.Request.Should().BeEquivalentTo(request);
    }

    [Fact]
    public async Task SaveDecisions_UnknownEscalation_Throws()
    {
        var store = CreateStore();

        var act = () => store.SaveDecisionsAsync(Guid.NewGuid(), [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SaveDecisions_PersistsDecisionList()
    {
        var store = CreateStore();
        var request = CreateRequest();
        await store.SavePendingAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
        var decision = new ApproverDecision
        {
            ApproverName = "approver-1",
            Approved = true,
            RespondedAt = DateTimeOffset.UtcNow
        };

        await store.SaveDecisionsAsync(request.EscalationId, [decision], CancellationToken.None);
        var active = await store.GetActiveAsync(CancellationToken.None);

        active.Should().ContainSingle()
            .Which.Decisions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(decision);
    }

    [Fact]
    public async Task MarkResolvedPendingAudit_OutcomeVisibleToReconcile_ButNotToPollers()
    {
        var store = CreateStore();
        var request = CreateRequest();
        await store.SavePendingAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
        var outcome = CreateOutcome(request.EscalationId);

        await store.MarkResolvedPendingAuditAsync(outcome, CancellationToken.None);

        var active = await store.GetActiveAsync(CancellationToken.None);
        var snapshot = active.Should().ContainSingle().Subject;
        snapshot.Status.Should().Be(EscalationPersistedStatus.ResolvedPendingAudit);
        snapshot.Outcome.Should().BeEquivalentTo(outcome);

        // Fail-closed: an outcome whose audit write has not completed must never be served.
        var polled = await store.GetResolvedOutcomeAsync(request.EscalationId, CancellationToken.None);
        polled.Should().BeNull();
    }

    [Fact]
    public async Task MarkResolved_OutcomeServedToPollers_AndExcludedFromActive()
    {
        var store = CreateStore();
        var request = CreateRequest();
        await store.SavePendingAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
        var outcome = CreateOutcome(request.EscalationId);
        await store.MarkResolvedPendingAuditAsync(outcome, CancellationToken.None);

        await store.MarkResolvedAsync(request.EscalationId, CancellationToken.None);

        (await store.GetActiveAsync(CancellationToken.None)).Should().BeEmpty();
        var polled = await store.GetResolvedOutcomeAsync(request.EscalationId, CancellationToken.None);
        polled.Should().BeEquivalentTo(outcome);
    }

    [Fact]
    public async Task Remove_DeletesRecord_AndIsNoOpForUnknownId()
    {
        var store = CreateStore();
        var request = CreateRequest();
        await store.SavePendingAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);

        await store.RemoveAsync(request.EscalationId, CancellationToken.None);
        await store.RemoveAsync(Guid.NewGuid(), CancellationToken.None);

        (await store.GetActiveAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetActive_AfterNewStoreInstanceOverSameDatabase_SurvivesRestart()
    {
        var request = CreateRequest();
        await CreateStore().SavePendingAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);

        // Simulated restart: a brand-new store (and context factory) over the same file.
        var active = await CreateStore().GetActiveAsync(CancellationToken.None);

        active.Should().ContainSingle()
            .Which.Request.EscalationId.Should().Be(request.EscalationId);
    }

    /// <summary>
    /// Minimal <see cref="IDbContextFactory{TContext}"/> over fixed options, mirroring the
    /// planner store tests' factory double.
    /// </summary>
    private sealed class TestContextFactory(DbContextOptions<GovernanceStateDbContext> options)
        : IDbContextFactory<GovernanceStateDbContext>
    {
        public GovernanceStateDbContext CreateDbContext() => new(options);
    }
}
