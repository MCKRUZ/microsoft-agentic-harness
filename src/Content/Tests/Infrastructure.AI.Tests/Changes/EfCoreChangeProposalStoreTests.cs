using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Tests.Changes.Support;
using Infrastructure.AI.Tests.Escalation.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

/// <summary>
/// Tests for <see cref="EfCoreChangeProposalStore"/>: contract parity with
/// <see cref="InMemoryChangeProposalStore"/> (round-trip, idempotent upsert, list filters,
/// ordering, caps), durability across simulated restarts (a new store instance over the same
/// database file), and polymorphic <see cref="ChangeTarget"/> round-tripping.
/// </summary>
public sealed class EfCoreChangeProposalStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<GovernanceStateDbContext> _options;

    public EfCoreChangeProposalStoreTests()
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

    private EfCoreChangeProposalStore CreateStore(int maxPayloadBytes = 1024 * 1024)
    {
        var factory = new TestContextFactory(_options);
        return new EfCoreChangeProposalStore(
            factory,
            new SchemaInitializer<GovernanceStateDbContext>(factory),
            _sealer,
            GovernanceStateTestConfig.Monitor(maxPayloadBytes: maxPayloadBytes),
            NullLogger<EfCoreChangeProposalStore>.Instance);
    }

    // ===== Contract parity with the in-memory store =====

    [Fact]
    public async Task SaveAndGet_RoundTripsFullAggregate()
    {
        var store = CreateStore();
        var proposal = TestProposals.NewProposal() with
        {
            History =
            [
                new GateDecision
                {
                    Timestamp = TestProposals.DefaultTime.AddMinutes(1),
                    GateKey = "self_validation",
                    Action = GateAction.Pass,
                    Reason = "looks fine",
                    DurationMs = 12
                }
            ]
        };

        await store.SaveAsync(proposal, CancellationToken.None);
        var fetched = await store.GetAsync(proposal.Id, CancellationToken.None);

        fetched.Should().BeEquivalentTo(proposal);
    }

    [Fact]
    public async Task Save_Idempotent_OverwritesByLastWrite()
    {
        var store = CreateStore();
        var initial = TestProposals.NewProposal();
        await store.SaveAsync(initial, CancellationToken.None);
        var updated = initial with { Status = ChangeProposalStatus.Validating };

        await store.SaveAsync(updated, CancellationToken.None);
        var fetched = await store.GetAsync(initial.Id, CancellationToken.None);

        fetched!.Status.Should().Be(ChangeProposalStatus.Validating);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        var store = CreateStore();
        var fetched = await store.GetAsync("missing", CancellationToken.None);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var store = CreateStore();
        var draft = TestProposals.NewProposal();
        var awaiting = draft with { Id = draft.Id + "-2", Status = ChangeProposalStatus.AwaitingApproval };
        await store.SaveAsync(draft, CancellationToken.None);
        await store.SaveAsync(awaiting, CancellationToken.None);

        var results = await store.ListAsync(
            new ChangeProposalQuery { Status = ChangeProposalStatus.AwaitingApproval },
            CancellationToken.None);

        results.Should().ContainSingle().Which.Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
    }

    [Fact]
    public async Task List_FiltersBySubmitterBlastRadiusAndTargetKind()
    {
        var store = CreateStore();
        var low = TestProposals.NewProposal(blastRadius: BlastRadius.Low);
        var high = TestProposals.NewProposal(blastRadius: BlastRadius.High) with { Id = low.Id + "-high" };
        var otherAgent = low with
        {
            Id = low.Id + "-other",
            SubmittedBy = TestProposals.DefaultIdentity with { Id = "agent-002" }
        };
        await store.SaveAsync(low, CancellationToken.None);
        await store.SaveAsync(high, CancellationToken.None);
        await store.SaveAsync(otherAgent, CancellationToken.None);

        var byRadius = await store.ListAsync(
            new ChangeProposalQuery { MinimumBlastRadius = BlastRadius.High },
            CancellationToken.None);
        var bySubmitter = await store.ListAsync(
            new ChangeProposalQuery { SubmittedByAgentId = "agent-002" },
            CancellationToken.None);
        var byKind = await store.ListAsync(
            new ChangeProposalQuery { TargetKind = ChangeTargetKind.GitRepo },
            CancellationToken.None);

        byRadius.Should().ContainSingle().Which.Id.Should().Be(high.Id);
        bySubmitter.Should().ContainSingle().Which.Id.Should().Be(otherAgent.Id);
        byKind.Should().HaveCount(3);
    }

    [Fact]
    public async Task List_OrdersMostRecentFirst_AndRespectsMaxResults()
    {
        var store = CreateStore();
        var baseline = TestProposals.NewProposal();
        for (var i = 0; i < 5; i++)
        {
            var p = baseline with
            {
                Id = $"id-{i}",
                SubmittedAt = TestProposals.DefaultTime.AddMinutes(i)
            };
            await store.SaveAsync(p, CancellationToken.None);
        }

        var results = await store.ListAsync(
            new ChangeProposalQuery { MaxResults = 2 },
            CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Id.Should().Be("id-4");
        results[1].Id.Should().Be("id-3");
    }

    [Fact]
    public async Task List_NonPositiveMaxResults_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.SaveAsync(TestProposals.NewProposal(), CancellationToken.None);

        var results = await store.ListAsync(
            new ChangeProposalQuery { MaxResults = 0 },
            CancellationToken.None);

        results.Should().BeEmpty();
    }

    // ===== Durability across restart =====

    [Fact]
    public async Task Get_AfterNewStoreInstanceOverSameDatabase_SurvivesRestart()
    {
        var proposal = TestProposals.NewProposal() with { Status = ChangeProposalStatus.AwaitingApproval };
        await CreateStore().SaveAsync(proposal, CancellationToken.None);

        // Simulated restart: a brand-new store (and context factory) over the same file.
        var rebooted = CreateStore();
        var fetched = await rebooted.GetAsync(proposal.Id, CancellationToken.None);
        var pending = await rebooted.ListAsync(
            new ChangeProposalQuery { Status = ChangeProposalStatus.AwaitingApproval },
            CancellationToken.None);

        fetched.Should().BeEquivalentTo(proposal);
        pending.Should().ContainSingle().Which.Id.Should().Be(proposal.Id);
    }

    // ===== Polymorphic target round-trip =====

    [Fact]
    public async Task Save_KubernetesAndIacTargets_RoundTripWithCanonicalKey()
    {
        var store = CreateStore();
        var k8s = ChangeProposal.Create(
            new KubernetesResourceTarget("ctx", "apps/v1", "Deployment", "default", "api"),
            [new ChangeEdit { Op = Domain.AI.SkillTraining.EditOp.Replace, Target = "replicas: 1", Content = "replicas: 2" }],
            TestProposals.DefaultIdentity,
            "scale api",
            BlastRadius.Medium,
            [WellKnownGateKeys.Approval],
            TestProposals.DefaultTime);
        var iac = ChangeProposal.Create(
            new IacDeploymentTarget("bicep", "core-net", "modules/net", "prod"),
            [new ChangeEdit { Op = Domain.AI.SkillTraining.EditOp.Append, Content = "tag: v2" }],
            TestProposals.DefaultIdentity,
            "tag deployment",
            BlastRadius.High,
            [WellKnownGateKeys.Approval],
            TestProposals.DefaultTime);
        await store.SaveAsync(k8s, CancellationToken.None);
        await store.SaveAsync(iac, CancellationToken.None);

        var k8sBack = await store.GetAsync(k8s.Id, CancellationToken.None);
        var iacBack = await store.GetAsync(iac.Id, CancellationToken.None);

        k8sBack!.Target.Should().BeOfType<KubernetesResourceTarget>();
        k8sBack.Target.CanonicalKey().Should().Be(k8s.Target.CanonicalKey());
        iacBack!.Target.Should().BeOfType<IacDeploymentTarget>();
        iacBack.Target.CanonicalKey().Should().Be(iac.Target.CanonicalKey());
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
