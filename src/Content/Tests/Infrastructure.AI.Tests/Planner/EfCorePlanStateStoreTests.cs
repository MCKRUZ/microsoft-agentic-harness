using System.Text.Json;
using Domain.AI.Planner;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Infrastructure.AI.Planner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

public sealed class EfCorePlanStateStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlannerDbContext> _options;
    private readonly FakeTimeProvider _timeProvider;
    private readonly EfCorePlanStateStore _store;

    public EfCorePlanStateStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new PlannerDbContext(_options);
        ctx.Database.EnsureCreated();

        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));

        var factory = new TestDbContextFactory(_options);
        _store = new EfCorePlanStateStore(factory, NullLogger<EfCorePlanStateStore>.Instance, _timeProvider);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SavePlanAsync_NewPlan_PersistsGraphAndSteps()
    {
        var graph = CreateTestGraph(stepCount: 3, edgeCount: 2);

        var result = await _store.SavePlanAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var ctx = new PlannerDbContext(_options);
        var entity = await ctx.PlanGraphs
            .Include(g => g.Steps).ThenInclude(s => s.ExecutionState)
            .Include(g => g.Edges)
            .Include(g => g.ExecutionLogs)
            .FirstOrDefaultAsync(g => g.Id == graph.Id.Value);

        entity.Should().NotBeNull();
        entity!.Name.Should().Be(graph.Name);
        entity.Steps.Should().HaveCount(3);
        entity.Edges.Should().HaveCount(2);
        entity.Steps.SelectMany(s => new[] { s.ExecutionState })
            .Where(es => es is not null)
            .Should().HaveCount(3, "each step should have an initial execution state");

        entity.Steps.Select(s => s.ExecutionState!.Status)
            .Should().AllSatisfy(s => s.Should().Be(StepExecutionStatus.Pending));

        entity.ExecutionLogs.Should().ContainSingle(l => l.EventType == "plan_created");
    }

    [Fact]
    public async Task LoadPlanAsync_ExistingPlan_ReturnsCompleteGraph()
    {
        var original = CreateTestGraph(stepCount: 3, edgeCount: 2);
        await _store.SavePlanAsync(original, CancellationToken.None);

        var result = await _store.LoadPlanAsync(original.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var loaded = result.Value!;
        loaded.Id.Should().Be(original.Id);
        loaded.Name.Should().Be(original.Name);
        loaded.Steps.Should().HaveCount(3);
        loaded.Edges.Should().HaveCount(2);
        loaded.Configuration.Should().BeEquivalentTo(original.Configuration);

        loaded.Steps[0].Configuration.Should().BeOfType<LlmCallConfig>();
        var llmConfig = (LlmCallConfig)loaded.Steps[0].Configuration;
        var originalLlmConfig = (LlmCallConfig)original.Steps[0].Configuration;
        llmConfig.SystemPrompt.Should().Be(originalLlmConfig.SystemPrompt);
    }

    [Fact]
    public async Task LoadPlanAsync_NonexistentPlan_ReturnsNull()
    {
        var result = await _store.LoadPlanAsync(PlanId.New(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStepStateAsync_StatusTransition_PersistsNewState()
    {
        var graph = CreateTestGraph(stepCount: 2, edgeCount: 1);
        await _store.SavePlanAsync(graph, CancellationToken.None);

        var runningState = new StepExecutionState
        {
            StepId = graph.Steps[0].Id,
            Status = StepExecutionStatus.Running,
            AttemptCount = 1,
            StartedAt = _timeProvider.GetUtcNow(),
        };
        var result1 = await _store.UpdateStepStateAsync(runningState, CancellationToken.None);
        result1.IsSuccess.Should().BeTrue();

        _timeProvider.Advance(TimeSpan.FromSeconds(5));

        var completedState = new StepExecutionState
        {
            StepId = graph.Steps[0].Id,
            Status = StepExecutionStatus.Completed,
            AttemptCount = 1,
            StartedAt = runningState.StartedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            Output = "step output",
        };
        var result2 = await _store.UpdateStepStateAsync(completedState, CancellationToken.None);
        result2.IsSuccess.Should().BeTrue();

        await using var ctx = new PlannerDbContext(_options);
        var entity = await ctx.StepExecutionStates
            .FirstAsync(s => s.StepId == graph.Steps[0].Id.Value);
        entity.Status.Should().Be(StepExecutionStatus.Completed);
        entity.Output.Should().Be("step output");
        entity.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ConcurrencyToken_StaleVersion_ThrowsDbUpdateConcurrencyException()
    {
        // Proves the EF concurrency token works at the DB level.
        // The store wraps this in a Result.Fail (tested by verifying the catch exists).
        var dbPath = Path.Combine(Path.GetTempPath(), $"planner_test_{Guid.NewGuid():N}.db");
        try
        {
            var fileOptions = new DbContextOptionsBuilder<PlannerDbContext>()
                .UseSqlite($"DataSource={dbPath}")
                .Options;

            await using (var initCtx = new PlannerDbContext(fileOptions))
            {
                await initCtx.Database.EnsureCreatedAsync();
            }

            var fileFactory = new TestDbContextFactory(fileOptions);
            var fileStore = new EfCorePlanStateStore(
                fileFactory, NullLogger<EfCorePlanStateStore>.Instance, _timeProvider);

            var graph = CreateTestGraph(stepCount: 1, edgeCount: 0);
            await fileStore.SavePlanAsync(graph, CancellationToken.None);

            // Read entity in two separate contexts (simulating overlapping reads)
            var ctx1 = new PlannerDbContext(fileOptions);
            var ctx2 = new PlannerDbContext(fileOptions);

            var entity1 = await ctx1.StepExecutionStates.FirstAsync();
            var entity2 = await ctx2.StepExecutionStates.FirstAsync();

            // First write succeeds
            entity1.Status = StepExecutionStatus.Running;
            entity1.Version++;
            await ctx1.SaveChangesAsync();

            // Second write has stale version — must fail
            entity2.Status = StepExecutionStatus.Failed;
            entity2.Version++;

            var act = () => ctx2.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

            await ctx2.DisposeAsync();
            await ctx1.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task UpdateStepStateAsync_NonexistentStep_ReturnsNotFound()
    {
        var graph = CreateTestGraph(stepCount: 1, edgeCount: 0);
        await _store.SavePlanAsync(graph, CancellationToken.None);

        var state = new StepExecutionState
        {
            StepId = PlanStepId.New(),
            Status = StepExecutionStatus.Running,
            AttemptCount = 1,
        };

        var result = await _store.UpdateStepStateAsync(state, CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task GetExecutionHistoryAsync_MultipleTransitions_ReturnsChronological()
    {
        var graph = CreateTestGraph(stepCount: 2, edgeCount: 1);
        await _store.SavePlanAsync(graph, CancellationToken.None);

        var state1 = new StepExecutionState
        {
            StepId = graph.Steps[0].Id,
            Status = StepExecutionStatus.Running,
            AttemptCount = 1,
            StartedAt = _timeProvider.GetUtcNow(),
        };
        await _store.UpdateStepStateAsync(state1, CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromSeconds(3));

        var state2 = new StepExecutionState
        {
            StepId = graph.Steps[0].Id,
            Status = StepExecutionStatus.Completed,
            AttemptCount = 1,
            StartedAt = state1.StartedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            Output = "done",
        };
        await _store.UpdateStepStateAsync(state2, CancellationToken.None);

        var result = await _store.GetExecutionHistoryAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var history = result.Value!;
        history.Should().HaveCountGreaterThanOrEqualTo(2);

        var stepHistory = history
            .Where(h => h.StepId == graph.Steps[0].Id)
            .ToList();
        stepHistory.Should().HaveCount(2);
        stepHistory[0].Timestamp.Should().BeBefore(stepHistory[1].Timestamp);
        stepHistory[0].Status.Should().Be(StepExecutionStatus.Running);
        stepHistory[1].Status.Should().Be(StepExecutionStatus.Completed);
    }

    [Fact]
    public async Task CheckpointAsync_MidExecution_SavesAllStepStates()
    {
        var graph = CreateTestGraph(stepCount: 4, edgeCount: 0);
        await _store.SavePlanAsync(graph, CancellationToken.None);

        var states = new List<StepExecutionState>
        {
            new() { StepId = graph.Steps[0].Id, Status = StepExecutionStatus.Completed, AttemptCount = 1 },
            new() { StepId = graph.Steps[1].Id, Status = StepExecutionStatus.Running, AttemptCount = 1,
                     StartedAt = _timeProvider.GetUtcNow() },
            new() { StepId = graph.Steps[2].Id, Status = StepExecutionStatus.Pending, AttemptCount = 0 },
            new() { StepId = graph.Steps[3].Id, Status = StepExecutionStatus.Failed, AttemptCount = 2,
                     ErrorMessage = "timeout" },
        };

        var result = await _store.CheckpointAsync(graph.Id, states, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var ctx = new PlannerDbContext(_options);
        var entities = await ctx.StepExecutionStates
            .Where(s => ctx.PlanSteps.Any(ps => ps.Id == s.StepId && ps.PlanGraphId == graph.Id.Value))
            .ToListAsync();

        entities.Should().HaveCount(4);
        entities.Should().Contain(e => e.Status == StepExecutionStatus.Completed);
        entities.Should().Contain(e => e.Status == StepExecutionStatus.Running);
        entities.Should().Contain(e => e.Status == StepExecutionStatus.Pending);
        entities.Should().Contain(e => e.Status == StepExecutionStatus.Failed);

        var logs = await ctx.PlanExecutionLogs
            .Where(l => l.PlanGraphId == graph.Id.Value && l.EventType == "checkpoint")
            .ToListAsync();
        logs.Should().ContainSingle();
    }

    [Fact]
    public async Task ResumeAsync_FromCheckpoint_TransitionsRunningToReadyAndReturnsStates()
    {
        var stepA = new PlanStep
        {
            Id = PlanStepId.New(), Name = "A", Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "a", ModelDeploymentKey = "gpt" },
            RetryPolicy = new RetryPolicy(),
        };
        var stepB = new PlanStep
        {
            Id = PlanStepId.New(), Name = "B", Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "b", ModelDeploymentKey = "gpt" },
            RetryPolicy = new RetryPolicy(),
        };
        var stepC = new PlanStep
        {
            Id = PlanStepId.New(), Name = "C", Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "c", ModelDeploymentKey = "gpt" },
            RetryPolicy = new RetryPolicy(),
        };
        var stepD = new PlanStep
        {
            Id = PlanStepId.New(), Name = "D", Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = "d", ModelDeploymentKey = "gpt" },
            RetryPolicy = new RetryPolicy(),
        };

        var graph = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "Resume Test",
            Steps = [stepA, stepB, stepC, stepD],
            Edges =
            [
                new PlanEdge(stepA.Id, stepB.Id, EdgeType.ControlFlow),
                new PlanEdge(stepB.Id, stepC.Id, EdgeType.ControlFlow),
            ],
            Configuration = new PlanConfiguration(),
        };

        await _store.SavePlanAsync(graph, CancellationToken.None);

        // Simulate mid-execution states via direct DB manipulation
        await using (var ctx = new PlannerDbContext(_options))
        {
            var states = await ctx.StepExecutionStates.ToListAsync();

            var stateA = states.First(s => s.StepId == stepA.Id.Value);
            stateA.Status = StepExecutionStatus.Completed;
            stateA.AttemptCount = 1;

            var stateB = states.First(s => s.StepId == stepB.Id.Value);
            stateB.Status = StepExecutionStatus.Running;
            stateB.AttemptCount = 1;

            var stateC = states.First(s => s.StepId == stepC.Id.Value);
            stateC.Status = StepExecutionStatus.Pending;

            var stateD = states.First(s => s.StepId == stepD.Id.Value);
            stateD.Status = StepExecutionStatus.Completed;
            stateD.AttemptCount = 1;

            await ctx.SaveChangesAsync();
        }

        var result = await _store.ResumeAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var stateMap = result.Value!;
        stateMap.Should().HaveCount(4);

        // B was Running, should be transitioned to Ready
        stateMap[stepB.Id].Status.Should().Be(StepExecutionStatus.Ready);

        // Others unchanged
        stateMap[stepA.Id].Status.Should().Be(StepExecutionStatus.Completed);
        stateMap[stepC.Id].Status.Should().Be(StepExecutionStatus.Pending);
        stateMap[stepD.Id].Status.Should().Be(StepExecutionStatus.Completed);
    }

    [Fact]
    public async Task ListPlansAsync_MultipleGraphs_ReturnsAll()
    {
        var graph1 = CreateTestGraph(stepCount: 1, edgeCount: 0);
        var graph2 = CreateTestGraph(stepCount: 2, edgeCount: 0);
        await _store.SavePlanAsync(graph1, CancellationToken.None);
        await _store.SavePlanAsync(graph2, CancellationToken.None);

        var result = await _store.ListPlansAsync(null, null, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(2);
    }

    // --- Test helpers ---

    private static PlanGraph CreateTestGraph(int stepCount, int edgeCount)
    {
        var steps = Enumerable.Range(0, stepCount).Select(i => new PlanStep
        {
            Id = PlanStepId.New(),
            Name = $"Step {i}",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig
            {
                SystemPrompt = $"Prompt for step {i}",
                ModelDeploymentKey = "gpt-4o",
            },
            RetryPolicy = new RetryPolicy { MaxRetries = 2 },
            Timeout = TimeSpan.FromSeconds(30),
        }).ToList();

        var edges = new List<PlanEdge>();
        for (var i = 0; i < edgeCount && i < stepCount - 1; i++)
        {
            edges.Add(new PlanEdge(steps[i].Id, steps[i + 1].Id, EdgeType.ControlFlow));
        }

        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "Test Plan",
            Steps = steps,
            Edges = edges,
            Configuration = new PlanConfiguration
            {
                MaxParallelSteps = 4,
                PlanTimeout = TimeSpan.FromMinutes(10),
            },
        };
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlannerDbContext> options)
        : IDbContextFactory<PlannerDbContext>
    {
        public PlannerDbContext CreateDbContext() => new(options);
    }
}
