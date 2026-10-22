using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Orchestration;
using Domain.AI.Planner;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.BackgroundServices;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.BackgroundServices;

/// <summary>
/// The delegation and plan retention sweeps (#901) on their schedule: an expired record goes on the tick, and a period
/// under a day deletes nothing, because a reloaded value is not re-validated.
/// </summary>
public sealed class RetentionSweepServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Origin = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private readonly ObservableFakeClock _clock = new(Origin);
    private readonly AppConfig _config = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"retention-sweeps-{Guid.NewGuid():N}");
    private readonly SqliteConnection _planner = new("DataSource=:memory:");

    public RetentionSweepServiceTests()
    {
        _config.AI.Orchestration.Subagent.DelegationStoragePath = _dir;
        _config.AI.Orchestration.Subagent.DelegationRetention.SweepInterval = TimeSpan.FromHours(1);
        _config.AI.Planner.Retention.SweepInterval = TimeSpan.FromHours(1);
        _planner.Open();
    }

    public void Dispose()
    {
        _planner.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private IOptionsMonitor<AppConfig> Monitor()
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        return monitor.Object;
    }

    [Theory]
    [InlineData(90, 0)]
    [InlineData(0.5, 1)]
    public async Task Delegation_AnIdleSessionFileGoesOnTheTick_UnlessThePeriodIsUnderADay(double days, int filesLeft)
    {
        _config.AI.Orchestration.Subagent.DelegationRetention.RetainFor = TimeSpan.FromDays(days);
        using var store = new JsonlDelegationStore(Monitor(), NullLogger<JsonlDelegationStore>.Instance);
        await store.AppendAsync(Record());
        var file = Directory.GetFiles(_dir, "*.jsonl", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(file, Origin.UtcDateTime.AddDays(-100));
        using var service = new DelegationRetentionService(store, Monitor(), _clock, NullLogger<DelegationRetentionService>.Instance);

        await StartAndTickAsync(service);

        Directory.GetFiles(_dir, "*.jsonl", SearchOption.AllDirectories).Should().HaveCount(filesLeft);
    }

    [Theory]
    [InlineData(90, 0, false)]
    [InlineData(0.5, 1, false)]
    [InlineData(90, 1, true)]
    public async Task Plans_AnIdlePlanGoesOnTheTick_UnlessThePeriodIsUnderADayOrAScheduleRunsIt(
        double days, int plansLeft, bool scheduled)
    {
        _config.AI.Planner.Retention.RetainFor = TimeSpan.FromDays(days);
        var factory = new PlannerFactory(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_planner).Options);
        var savedAt = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Origin.AddDays(-100));
        var store = new EfCorePlanStateStore(
            factory, NullLogger<EfCorePlanStateStore>.Instance, savedAt, new Scope(), new SchemaInitializer<PlannerDbContext>(factory));
        var plan = Plan();
        (await store.SavePlanAsync(plan, CancellationToken.None)).IsSuccess.Should().BeTrue();

        // A quarterly schedule waits longer than the period between runs; the plan it runs must survive the wait. The
        // target is stored as the caller sent it, so it is upper-cased here to prove the match is by id, not by text.
        var schedules = new Mock<IScheduleStore>();
        schedules.Setup(s => s.ListTargetIdsAsync(Domain.AI.Runs.RunKind.Workflow, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scheduled ? [plan.Id.Value.ToString().ToUpperInvariant()] : []);
        var services = new ServiceCollection()
            .AddScoped<IPlanStateStore>(_ => store)
            .AddSingleton(schedules.Object)
            .BuildServiceProvider();
        using var service = new PlanRetentionService(
            services.GetRequiredService<IServiceScopeFactory>(), Monitor(), _clock, NullLogger<PlanRetentionService>.Instance);

        await StartAndTickAsync(service);

        await using var ctx = factory.CreateDbContext();
        (await ctx.PlanGraphs.CountAsync()).Should().Be(plansLeft);
    }

    [Theory]
    [InlineData(730, true)]
    [InlineData(0.5, false)]
    public async Task Memories_AnUnusedMemoryGoesOnTheTick_UnlessThePeriodIsUnderADay(double days, bool deleted)
    {
        _config.AI.Rag.CrossSessionMemory.Retention.RetainFor = TimeSpan.FromDays(days);
        _config.AI.Rag.CrossSessionMemory.Retention.SweepInterval = TimeSpan.FromHours(1);
        _config.AI.Rag.CrossSessionMemory.SyncInterval = TimeSpan.FromHours(1);
        var backend = new Mock<Application.AI.Common.Interfaces.KnowledgeGraph.IGraphDatabaseBackend>();
        var removed = new List<string>();
        backend.Setup(b => b.GetAllNodesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new Domain.AI.KnowledgeGraph.Models.GraphNode
            {
                Id = "old", Name = "old", Type = "Memory", OwnerId = "u1",
                Properties = new Dictionary<string, string> { ["last_accessed_at"] = Origin.AddDays(-800).ToString("O") },
            },
        ]);
        backend.Setup(b => b.DeleteNodesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, CancellationToken>((ids, _) => removed.AddRange(ids))
            .ReturnsAsync(Domain.AI.KnowledgeGraph.Models.NodeDeletionResult.Empty);
        using var memory = new Infrastructure.AI.RAG.GraphRag.CrossSessionMemoryStore(
            backend.Object, Monitor(), NullLogger<Infrastructure.AI.RAG.GraphRag.CrossSessionMemoryStore>.Instance);
        var services = new ServiceCollection()
            .AddSingleton<Application.AI.Common.Interfaces.KnowledgeGraph.ICrossSessionMemoryStore>(memory)
            .BuildServiceProvider();
        using var service = new Infrastructure.AI.Memory.CrossSessionMemoryRetentionService(
            services, Monitor(), _clock, NullLogger<Infrastructure.AI.Memory.CrossSessionMemoryRetentionService>.Instance);

        await StartAndTickAsync(service);

        removed.Contains("old").Should().Be(deleted);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(0.5, true)]
    public async Task Traces_AnIdleRunGoesOnTheTick_UnlessThePeriodIsUnderADay(double days, bool runLeft)
    {
        _config.MetaHarness.TraceDirectoryRoot = _dir;
        _config.MetaHarness.TraceRetention.RetainFor = TimeSpan.FromDays(days);
        _config.MetaHarness.TraceRetention.SweepInterval = TimeSpan.FromHours(1);
        var id = Guid.NewGuid().ToString("D");
        var run = Path.Combine(_dir, "executions", id);
        Directory.CreateDirectory(run);
        var manifest = Path.Combine(run, "manifest.json");
        await File.WriteAllTextAsync(manifest, $$"""{"execution_run_id":"{{id}}"}""");
        // Older than 30 days on the fake clock's calendar; the sweep compares against real file times.
        File.SetLastWriteTimeUtc(manifest, Origin.AddDays(-45).UtcDateTime);
        Directory.SetLastWriteTimeUtc(run, Origin.AddDays(-45).UtcDateTime);
        var store = new Infrastructure.AI.Traces.FileSystemExecutionTraceStore(
            Monitor(), Mock.Of<Application.AI.Common.Interfaces.ISecretRedactor>(),
            NullLogger<Infrastructure.AI.Traces.FileSystemExecutionTraceStore>.Instance);
        using var service = new Infrastructure.AI.Traces.ExecutionTraceRetentionService(
            store, Monitor(), _clock, NullLogger<Infrastructure.AI.Traces.ExecutionTraceRetentionService>.Instance);

        await StartAndTickAsync(service);

        Directory.Exists(run).Should().Be(runLeft);
    }

    [Theory]
    [InlineData(90, 1)]
    [InlineData(0.5, 0)]
    public async Task Observability_InactiveSessionsAreDeletedOnTheTick_UnlessThePeriodIsUnderADay(double days, int calls)
    {
        _config.Observability.Retention.RetainFor = TimeSpan.FromDays(days);
        _config.Observability.Retention.SweepInterval = TimeSpan.FromHours(1);
        var store = new Mock<Application.AI.Common.Interfaces.IObservabilityStore>();
        var swept = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Setup(s => s.DeleteInactiveSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, int, CancellationToken>((cutoff, _, _) => swept.TrySetResult(cutoff))
            .ReturnsAsync(0);
        var services = new ServiceCollection().AddSingleton(store.Object).BuildServiceProvider();
        using var service = new ObservabilityRetentionService(
            services, Monitor(), _clock, NullLogger<ObservabilityRetentionService>.Instance);

        await StartAndTickAsync(service);

        store.Verify(
            s => s.DeleteInactiveSinceAsync(
                It.IsAny<DateTimeOffset>(), ObservabilityRetentionService.MaxSessionsPerBatch, It.IsAny<CancellationToken>()),
            Times.Exactly(calls));
        if (calls > 0)
            (await swept.Task.WaitAsync(Patience)).Should().Be(_clock.GetUtcNow() - TimeSpan.FromDays(days));
    }

    [Fact]
    public async Task Observability_ABacklogLargerThanABatch_IsClearedInOneSweep()
    {
        // Without further batches a sweep removes one batch per interval, and a busier host grows its backlog forever
        // while the receipt still promises the period.
        _config.Observability.Retention.SweepInterval = TimeSpan.FromHours(1);
        var store = new Mock<Application.AI.Common.Interfaces.IObservabilityStore>();
        store.SetupSequence(s => s.DeleteInactiveSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ObservabilityRetentionService.MaxSessionsPerBatch)
            .ReturnsAsync(ObservabilityRetentionService.MaxSessionsPerBatch)
            .ReturnsAsync(3);
        var services = new ServiceCollection().AddSingleton(store.Object).BuildServiceProvider();
        using var service = new ObservabilityRetentionService(
            services, Monitor(), _clock, NullLogger<ObservabilityRetentionService>.Instance);

        await StartAndTickAsync(service);

        store.Verify(
            s => s.DeleteInactiveSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Theory]
    [InlineData(false, false, 90)]
    [InlineData(true, true, 90)]
    [InlineData(false, true, 0.5)]
    public async Task Evidence_AnOpenProposalsEvidenceIsKept_AndAnUnreadableStoreOrAPeriodUnderADayStopsTheSweep(
        bool storeFails, bool unreferencedLeft, double days)
    {
        // A proposal store that cannot say what every open proposal points at may be hiding one, and deleting the
        // evidence it points at cannot be undone, so the sweep deletes nothing rather than guess.
        _config.AI.Changes.EvidenceStoragePath = _dir;
        _config.AI.Changes.EvidenceRetention.RetainFor = TimeSpan.FromDays(days);
        _config.AI.Changes.EvidenceRetention.SweepInterval = TimeSpan.FromHours(1);
        var evidence = new Infrastructure.AI.Changes.FileSystemEvidenceStore(
            Monitor(), NullLogger<Infrastructure.AI.Changes.FileSystemEvidenceStore>.Instance);
        var referenced = await evidence.StoreAsync("referenced"u8.ToArray(), "text/plain", CancellationToken.None);
        var unreferenced = await evidence.StoreAsync("unreferenced"u8.ToArray(), "text/plain", CancellationToken.None);
        foreach (var hash in new[] { referenced, unreferenced })
        {
            var name = hash["sha256:".Length..];
            var blob = Path.Combine(_dir, name[..2], name + ".bin");
            File.SetLastWriteTimeUtc(blob, Origin.AddDays(-120).UtcDateTime);
            File.SetLastWriteTimeUtc(blob + ".contenttype", Origin.AddDays(-120).UtcDateTime);
        }

        Application.AI.Common.Interfaces.Changes.IChangeProposalStore proposals;
        if (storeFails)
        {
            var failing = new Mock<Application.AI.Common.Interfaces.Changes.IChangeProposalStore>();
            failing.Setup(p => p.GetEvidenceHashesOfOpenProposalsAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("an open proposal could not be read"));
            proposals = failing.Object;
        }
        else
        {
            proposals = new Infrastructure.AI.Changes.InMemoryChangeProposalStore();
            await proposals.SaveAsync(OpenProposalPointingAt(referenced), CancellationToken.None);
            await proposals.SaveAsync(
                OpenProposalPointingAt(unreferenced) with
                {
                    Id = "merged-proposal", Status = Domain.AI.Changes.ChangeProposalStatus.Merged,
                },
                CancellationToken.None);
        }

        var services = new ServiceCollection()
            .AddSingleton<Application.AI.Common.Interfaces.Changes.IEvidenceStore>(evidence)
            .AddSingleton(proposals)
            .BuildServiceProvider();
        using var service = new Infrastructure.AI.Changes.EvidenceRetentionService(
            services, Monitor(), _clock, NullLogger<Infrastructure.AI.Changes.EvidenceRetentionService>.Instance);

        await StartAndTickAsync(service);

        File.Exists(BlobFile(referenced)).Should().BeTrue();
        File.Exists(BlobFile(unreferenced)).Should().Be(unreferencedLeft);
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(0.5, true)]
    public async Task WorkflowState_AnIdleWorkflowGoesOnTheTick_UnlessThePeriodIsUnderADay(double days, bool stateLeft)
    {
        _config.Infrastructure.StateManagement.BasePath = _dir;
        _config.Infrastructure.StateManagement.Retention.RetainFor = TimeSpan.FromDays(days);
        _config.Infrastructure.StateManagement.Retention.SweepInterval = TimeSpan.FromHours(1);
        var infra = new Mock<IOptionsMonitor<Domain.Common.Config.Infrastructure.InfrastructureConfig>>();
        infra.Setup(m => m.CurrentValue).Returns(() => _config.Infrastructure);
        var checkpoints = new Infrastructure.AI.StateManagement.Checkpoints.JsonCheckpointStateManager(
            NullLogger<Infrastructure.AI.StateManagement.Checkpoints.JsonCheckpointStateManager>.Instance, infra.Object);
        await checkpoints.CreateAsync("idle");
        var state = Path.Combine(_dir, "idle", "checkpoints", "workflow-state.json");
        File.SetLastWriteTimeUtc(state, Origin.AddDays(-120).UtcDateTime);
        var services = new ServiceCollection().AddSingleton(checkpoints).BuildServiceProvider();
        using var service = new Infrastructure.AI.StateManagement.WorkflowStateRetentionService(
            services, Monitor(), _clock, NullLogger<Infrastructure.AI.StateManagement.WorkflowStateRetentionService>.Instance);

        await StartAndTickAsync(service);

        File.Exists(state).Should().Be(stateLeft);
    }

    private string BlobFile(string hash)
    {
        var name = hash["sha256:".Length..];
        return Path.Combine(_dir, name[..2], name + ".bin");
    }

    private static Domain.AI.Changes.ChangeProposal OpenProposalPointingAt(string evidenceHash) =>
        Domain.AI.Changes.ChangeProposal.Create(
            target: new Domain.AI.Changes.GitRepoTarget("https://github.com/org/repo", "main", "abc123"),
            diff: [],
            submittedBy: new Domain.AI.Identity.AgentIdentity
            {
                Id = "agent-001", Kind = Domain.AI.Identity.AgentIdentityKind.ManagedIdentity, TenantId = "tenant-A",
            },
            summary: "a proposal awaiting approval",
            blastRadius: Domain.AI.Changes.BlastRadius.Low,
            requiredGates: [],
            submittedAt: Origin) with
        {
            Status = Domain.AI.Changes.ChangeProposalStatus.AwaitingApproval,
            History =
            [
                new Domain.AI.Changes.GateDecision
                {
                    Timestamp = Origin, GateKey = "gate", Action = Domain.AI.Changes.GateAction.Pass,
                    EvidenceHash = evidenceHash, DurationMs = 1,
                },
            ],
        };

    [Fact]
    public async Task ASweep_StopsAfterItsBatchLimit_EvenWhileBatchesComeBackFull()
    {
        var calls = 0;

        var total = await SweepBatches.RunAsync(() => Task.FromResult(++calls > 0 ? 10 : 0), batchSize: 10);

        calls.Should().Be(SweepBatches.MaxBatches);
        total.Should().Be(10 * SweepBatches.MaxBatches);
    }

    [Fact]
    public async Task Observability_WithNoStoreRegistered_TheSweepDoesNothing()
    {
        _config.Observability.Retention.SweepInterval = TimeSpan.FromHours(1);
        using var service = new ObservabilityRetentionService(
            new ServiceCollection().BuildServiceProvider(), Monitor(), _clock,
            NullLogger<ObservabilityRetentionService>.Instance);

        var act = () => StartAndTickAsync(service);

        await act.Should().NotThrowAsync();
    }

    private async Task StartAndTickAsync(Microsoft.Extensions.Hosting.BackgroundService service)
    {
        var armed = _clock.NextTimer;
        await service.StartAsync(CancellationToken.None);
        await armed.WaitAsync(Patience);
        var reArmed = _clock.NextTimer;
        _clock.Advance(TimeSpan.FromHours(1));
        await reArmed.WaitAsync(Patience);
    }

    private static DelegationRecord Record() => new()
    {
        DelegationId = Guid.NewGuid(),
        SupervisorId = "supervisor",
        DelegateAgentId = "agent",
        DelegateAgentType = Domain.AI.Agents.SubagentType.Execute,
        TaskDescription = "task",
        RequiredCapabilities = [],
        AutonomyLevel = Domain.AI.Governance.AutonomyLevel.Supervised,
        State = DelegationState.Pending,
        DelegationDepth = 0,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static PlanGraph Plan()
    {
        var step = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "Step",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "Prompt", ModelDeploymentKey = "gpt-4o" },
            RetryPolicy = new RetryPolicy { MaxRetries = 0 },
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "Plan",
            Steps = [step],
            Edges = [],
            Configuration = new PlanConfiguration { MaxParallelSteps = 1, PlanTimeout = TimeSpan.FromMinutes(10) },
        };
    }

    private sealed class Scope : IKnowledgeScope
    {
        public string? UserId => "alice";
        public string? TenantId => null;
        public string? DatasetId => null;
        public string? DatasetName => null;
        public string? DatasetOwnerId => null;
        public string? AgentId => null;
        public string? ConversationId => null;
    }

    private sealed class PlannerFactory(DbContextOptions<PlannerDbContext> options) : IDbContextFactory<PlannerDbContext>
    {
        public PlannerDbContext CreateDbContext() => new(options);
    }
}
