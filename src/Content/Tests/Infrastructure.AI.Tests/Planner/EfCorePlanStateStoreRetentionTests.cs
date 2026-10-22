using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.Planner;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Plan retention (#901): a plan nothing has happened to since the cutoff leaves with every child row, and any activity
/// after it (a save, a step starting or finishing, a log entry) keeps the plan.
/// </summary>
/// <remarks>Each kind of activity has its own test, so dropping any one of them from the idle check fails one.</remarks>
public sealed class EfCorePlanStateStoreRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection;
    private readonly Factory _factory;
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly EfCorePlanStateStore _store;

    public EfCorePlanStateStoreRetentionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new Factory(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_connection).Options);
        _store = new EfCorePlanStateStore(
            _factory, NullLogger<EfCorePlanStateStore>.Instance, _clock, new Scope(), new SchemaInitializer<PlannerDbContext>(_factory));
    }

    public void Dispose() => _connection.Dispose();

    private DateTimeOffset Cutoff => _clock.GetUtcNow() - TimeSpan.FromDays(90);

    [Fact]
    public async Task APlanNothingHasHappenedTo_IsDeletedWithEveryChildRow_AndARecentOneIsKept()
    {
        var old = await SaveRunPlanAsync();
        _clock.Advance(TimeSpan.FromDays(100));
        var recent = await SaveRunPlanAsync();

        var removed = await _store.DeleteIdleSinceAsync(Cutoff, CancellationToken.None);

        removed.Should().Be(1);
        (await CountAsync(old)).Should().Be((0, 0, 0, 0, 0), "no child row of an expired plan may survive");
        (await CountAsync(recent)).Plans.Should().Be(1);
    }

    [Fact]
    public async Task AStepThatStartedAfterTheCutoff_KeepsAnOldPlan()
    {
        var plan = await SaveRunPlanAsync();
        _clock.Advance(TimeSpan.FromDays(100));
        await SetActivityAsync(plan, startedAt: _clock.GetUtcNow());

        (await _store.DeleteIdleSinceAsync(Cutoff, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task AStepThatFinishedAfterTheCutoff_KeepsAnOldPlan()
    {
        var plan = await SaveRunPlanAsync();
        _clock.Advance(TimeSpan.FromDays(100));
        await SetActivityAsync(plan, completedAt: _clock.GetUtcNow());

        (await _store.DeleteIdleSinceAsync(Cutoff, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task ALogEntryAfterTheCutoff_KeepsAnOldPlan()
    {
        var plan = await SaveRunPlanAsync();
        _clock.Advance(TimeSpan.FromDays(100));
        await SetActivityAsync(plan, logAt: _clock.GetUtcNow());

        (await _store.DeleteIdleSinceAsync(Cutoff, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task APlanNeverRun_IsDeletedOnceIdle()
    {
        var neverRun = CreateGraph();
        (await _store.SavePlanAsync(neverRun, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(91));

        (await _store.DeleteIdleSinceAsync(Cutoff, CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task APlanSavedAfterTheCutoffButNeverRun_IsKept()
    {
        // Saving is activity: a plan saved to run later is not idle until the period has passed since it was saved.
        _clock.Advance(TimeSpan.FromDays(100));
        (await _store.SavePlanAsync(CreateGraph(), CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await _store.DeleteIdleSinceAsync(Cutoff, CancellationToken.None)).Should().Be(0);
    }

    /// <summary>Saves a plan and records a step state and log at the clock's current time, as a run does.</summary>
    private async Task<PlanGraph> SaveRunPlanAsync()
    {
        var graph = CreateGraph();
        (await _store.SavePlanAsync(graph, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await _store.UpdateStepStateAsync(
                new StepExecutionState
                {
                    StepId = graph.Steps[0].Id,
                    Status = StepExecutionStatus.Completed,
                    AttemptCount = 1,
                    StartedAt = _clock.GetUtcNow(),
                    CompletedAt = _clock.GetUtcNow(),
                },
                CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        return graph;
    }

    /// <summary>Moves one kind of activity forward directly, leaving the others as they were saved.</summary>
    private async Task SetActivityAsync(
        PlanGraph plan, DateTimeOffset? startedAt = null, DateTimeOffset? completedAt = null, DateTimeOffset? logAt = null)
    {
        await using var ctx = _factory.CreateDbContext();
        var stepId = plan.Steps[0].Id.Value;
        var state = await ctx.StepExecutionStates.SingleAsync(s => s.StepId == stepId);
        state.StartedAt = startedAt ?? state.StartedAt;
        state.CompletedAt = completedAt ?? state.CompletedAt;
        if (logAt is { } at)
        {
            var log = await ctx.PlanExecutionLogs.FirstAsync(l => l.PlanGraphId == plan.Id.Value);
            log.Timestamp = at;
        }

        await ctx.SaveChangesAsync();
    }

    private async Task<(int Plans, int Steps, int Edges, int States, int Logs)> CountAsync(PlanGraph plan)
    {
        await using var ctx = _factory.CreateDbContext();
        var id = plan.Id.Value;
        var stepIds = plan.Steps.Select(s => s.Id.Value).ToArray();
        return (
            await ctx.PlanGraphs.CountAsync(p => p.Id == id),
            await ctx.PlanSteps.CountAsync(s => s.PlanGraphId == id),
            await ctx.PlanEdges.CountAsync(e => e.PlanGraphId == id),
            await ctx.StepExecutionStates.CountAsync(s => stepIds.Contains(s.StepId)),
            await ctx.PlanExecutionLogs.CountAsync(l => l.PlanGraphId == id));
    }

    private static PlanGraph CreateGraph()
    {
        var steps = Enumerable.Range(0, 2).Select(i => new PlanStep
        {
            Id = PlanStepId.New(),
            Name = $"Step {i}",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = $"Prompt {i}", ModelDeploymentKey = "gpt-4o" },
            RetryPolicy = new RetryPolicy { MaxRetries = 2 },
            Timeout = TimeSpan.FromSeconds(30),
        }).ToList();
        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "Retention Test Plan",
            Steps = steps,
            Edges = [new PlanEdge(steps[0].Id, steps[1].Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { MaxParallelSteps = 4, PlanTimeout = TimeSpan.FromMinutes(10) },
        };
    }

    private sealed class Scope : IKnowledgeScope
    {
        public string? UserId => "alice";
        public string? TenantId => "acme";
        public string? DatasetId => null;
        public string? DatasetName => null;
        public string? DatasetOwnerId => null;
        public string? AgentId => null;
        public string? ConversationId => null;
    }

    private sealed class Factory(DbContextOptions<PlannerDbContext> options) : IDbContextFactory<PlannerDbContext>
    {
        public PlannerDbContext CreateDbContext() => new(options);
    }
}
