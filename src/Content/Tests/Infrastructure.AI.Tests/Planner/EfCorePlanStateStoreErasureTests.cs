using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.Planner;
using Domain.Common.Compliance;
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
/// Right-to-erasure (#900) for <see cref="EfCorePlanStateStore"/>: a person's plans leave the database together with
/// every step, edge, step execution state and execution log hanging off them, and no other owner's plan loses a row.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. A plan is five tables, so "the plan row is gone" proves little — every table is counted
/// directly. Plans store the owner canonicalized, so the subject is deliberately built from a differently-cased,
/// space-padded form of the id.
/// </remarks>
public sealed class EfCorePlanStateStoreErasureTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly StubKnowledgeScope _scope = new();
    private readonly EfCorePlanStateStore _store;

    public EfCorePlanStateStoreErasureTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_connection).Options);
        _store = new EfCorePlanStateStore(
            _factory,
            NullLogger<EfCorePlanStateStore>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)),
            _scope,
            new SchemaInitializer<PlannerDbContext>(_factory), PlannerTestConfig.Monitor());
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task EraseSubjectAsync_TwoOwners_RemovesEveryChildRowOfTheSubjectsPlansOnly()
    {
        var alice1 = await SaveWithHistoryAsync(" Alice ");
        var alice2 = await SaveWithHistoryAsync(" Alice ");
        var bob = await SaveWithHistoryAsync("bob");
        var aliceIds = new[] { alice1.Id.Value, alice2.Id.Value };
        var aliceSteps = alice1.Steps.Concat(alice2.Steps).Select(s => s.Id.Value).ToArray();
        var bobBefore = await CountRowsAsync([bob.Id.Value], bob.Steps.Select(s => s.Id.Value).ToArray());
        (await CountRowsAsync(aliceIds, aliceSteps)).Should().Be(
            new RowCounts(Plans: 2, Steps: 4, Edges: 2, States: 4, Logs: 4), "the seed must exist before erasing");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("ALICE ", []), CancellationToken.None);

        removed.Should().Be(2, "the return value counts the plans removed");
        (await CountRowsAsync(aliceIds, aliceSteps)).Should().Be(
            new RowCounts(0, 0, 0, 0, 0), "no orphan row may survive in any of the five tables");
        (await CountRowsAsync([bob.Id.Value], bob.Steps.Select(s => s.Id.Value).ToArray()))
            .Should().Be(bobBefore, "another owner's plan must keep every one of its rows");
    }

    [Fact]
    public async Task EraseSubjectAsync_OtherOwnersSubPlanOfAnErasedPlan_SurvivesWithItsParentLinkCleared()
    {
        var parent = await SaveWithHistoryAsync("alice");
        SetCaller("bob");
        var child = CreateTestGraph() with { ParentPlanId = parent.Id };
        (await _store.SavePlanAsync(child, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("alice", []), CancellationToken.None);

        removed.Should().Be(1);
        await using var ctx = _factory.CreateDbContext();
        var survivor = await ctx.PlanGraphs.SingleAsync(p => p.Id == child.Id.Value);
        survivor.ParentPlanId.Should().BeNull("the link to an erased parent is cleared, the sub-plan itself is kept");
        (await ctx.PlanSteps.CountAsync(s => s.PlanGraphId == child.Id.Value)).Should().Be(child.Steps.Count);
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerWithNothingStored_ReturnsZeroAndChangesNothing()
    {
        var bob = await SaveWithHistoryAsync("bob");
        var stepIds = bob.Steps.Select(s => s.Id.Value).ToArray();
        var before = await CountRowsAsync([bob.Id.Value], stepIds);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("nobody", []), CancellationToken.None);

        removed.Should().Be(0);
        (await CountRowsAsync([bob.Id.Value], stepIds)).Should().Be(before);
    }

    private async Task<PlanGraph> SaveWithHistoryAsync(string owner)
    {
        SetCaller(owner);
        var graph = CreateTestGraph();
        (await _store.SavePlanAsync(graph, CancellationToken.None)).IsSuccess.Should().BeTrue();

        // A second log row, this one carrying a step id, written by the owner the way an executor does.
        var update = await _store.UpdateStepStateAsync(
            new StepExecutionState { StepId = graph.Steps[0].Id, Status = StepExecutionStatus.Running, AttemptCount = 1 },
            CancellationToken.None);
        update.IsSuccess.Should().BeTrue();
        return graph;
    }

    private async Task<RowCounts> CountRowsAsync(Guid[] planIds, Guid[] stepIds)
    {
        await using var ctx = _factory.CreateDbContext();
        return new RowCounts(
            await ctx.PlanGraphs.CountAsync(p => planIds.Contains(p.Id)),
            await ctx.PlanSteps.CountAsync(s => planIds.Contains(s.PlanGraphId)),
            await ctx.PlanEdges.CountAsync(e => planIds.Contains(e.PlanGraphId)),
            await ctx.StepExecutionStates.CountAsync(s => stepIds.Contains(s.StepId)),
            await ctx.PlanExecutionLogs.CountAsync(l => planIds.Contains(l.PlanGraphId)));
    }

    private void SetCaller(string owner)
    {
        _scope.UserId = owner;
        _scope.TenantId = "acme";
    }

    private static PlanGraph CreateTestGraph()
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
            Name = "Erasure Test Plan",
            Steps = steps,
            Edges = [new PlanEdge(steps[0].Id, steps[1].Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { MaxParallelSteps = 4, PlanTimeout = TimeSpan.FromMinutes(10) },
        };
    }

    private sealed record RowCounts(int Plans, int Steps, int Edges, int States, int Logs);

    private sealed class StubKnowledgeScope : IKnowledgeScope
    {
        public string? UserId { get; set; }
        public string? TenantId { get; set; }
        public string? DatasetId => null;
        public string? DatasetName => null;
        public string? DatasetOwnerId => null;
        public string? AgentId => null;
        public string? ConversationId => null;
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlannerDbContext> options)
        : IDbContextFactory<PlannerDbContext>
    {
        public PlannerDbContext CreateDbContext() => new(options);
    }
}
