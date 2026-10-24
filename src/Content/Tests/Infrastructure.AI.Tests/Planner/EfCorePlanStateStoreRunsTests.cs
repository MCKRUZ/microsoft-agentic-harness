using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.Planner;
using Domain.Common.Compliance;
using Domain.Common.Config;
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
/// The plan store's record of who ran each plan under which ids (#906), which is the only thing that leads
/// right-to-erasure to a planned task's step sessions, spills and budgets.
/// </summary>
/// <remarks>
/// A step session the record does not lead to survives an erasure whose receipt says <c>Full</c>; one it wrongly leads to
/// is another person's data deleted. Every test checks both directions.
/// </remarks>
public sealed class EfCorePlanStateStoreRunsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly StubKnowledgeScope _scope = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly AppConfig _config = new();
    private readonly EfCorePlanStateStore _store;

    public EfCorePlanStateStoreRunsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_connection).Options);
        _store = new EfCorePlanStateStore(
            _factory, NullLogger<EfCorePlanStateStore>.Instance, _time, _scope,
            new SchemaInitializer<PlannerDbContext>(_factory), PlannerTestConfig.Monitor(_config));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ListRunTracesAsync_ARecordedRun_LeadsToEveryStepSessionItsToolScopeAndItsBudget()
    {
        var plan = await SaveAsync("alice");
        (await _store.RecordRunAsync(plan.Id, "conv-7", "job-1", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var traces = await _store.ListRunTracesAsync(" ALICE ", CancellationToken.None);

        // The run scope "conv-7" (a caller-supplied conversation) and the plan id (every workflow run's scope) both count.
        traces.StepConversationIds.Should().BeEquivalentTo(plan.Steps.SelectMany(s => new[]
        {
            PlanRunKeys.StepConversationId("conv-7", s.Id),
            PlanRunKeys.StepConversationId(plan.Id.Value.ToString(), s.Id),
        }));
        traces.ToolResultScopeIds.Should().Equal("job-1");
        traces.RunBudgetKeys.Should().BeEquivalentTo(
            [PlanRunKeys.RunBudgetKey("conv-7"), PlanRunKeys.RunBudgetKey(plan.Id.Value.ToString())]);
    }

    [Fact]
    public async Task ListRunTracesAsync_AnotherPersonsRuns_AreNotListed()
    {
        var alicePlan = await SaveAsync("alice");
        await _store.RecordRunAsync(alicePlan.Id, alicePlan.Id.Value.ToString(), "job-alice", CancellationToken.None);
        var bobPlan = await SaveAsync("bob");
        await _store.RecordRunAsync(bobPlan.Id, bobPlan.Id.Value.ToString(), "job-bob", CancellationToken.None);

        var traces = await _store.ListRunTracesAsync("alice", CancellationToken.None);

        traces.ToolResultScopeIds.Should().Equal("job-alice");
        traces.StepConversationIds.Should().NotContain(id => id.StartsWith(bobPlan.Id.Value.ToString(), StringComparison.Ordinal));
        traces.RunBudgetKeys.Should().Equal(PlanRunKeys.RunBudgetKey(alicePlan.Id.Value.ToString()));
    }

    [Fact]
    public async Task ASharedSubPlanRunInsideSomeonesWorkflow_IsTheirs_AndErasingThemKeepsThePlanAndOthersRuns()
    {
        // A plan with no owner, run by two people as a sub-plan under each one's own workflow scope.
        var shared = await SaveAsync(null);
        SetCaller("alice");
        await _store.RecordRunAsync(shared.Id, "alice-root", "job-alice", CancellationToken.None);
        SetCaller("bob");
        await _store.RecordRunAsync(shared.Id, "bob-root", "job-bob", CancellationToken.None);

        var aliceTraces = await _store.ListRunTracesAsync("alice", CancellationToken.None);
        aliceTraces.StepConversationIds.Should().BeEquivalentTo(
            shared.Steps.Select(s => PlanRunKeys.StepConversationId("alice-root", s.Id)));

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("alice", []), CancellationToken.None);

        removed.Should().Be(1, "alice's one run record, and no plan, since she owns none");
        (await _store.ListRunTracesAsync("alice", CancellationToken.None)).IsEmpty.Should().BeTrue();
        (await _store.ListRunTracesAsync("bob", CancellationToken.None)).ToolResultScopeIds.Should().Equal("job-bob");
        await using var ctx = _factory.CreateDbContext();
        (await ctx.PlanGraphs.CountAsync(p => p.Id == shared.Id.Value)).Should().Be(1, "the shared plan is not alice's");
    }

    [Fact]
    public async Task EraseSubjectAsync_RemovesThePersonsPlansWithTheirRunRecords()
    {
        var plan = await SaveAsync("alice");
        await _store.RecordRunAsync(plan.Id, plan.Id.Value.ToString(), "job-1", CancellationToken.None);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("alice", []), CancellationToken.None);

        removed.Should().Be(2, "one plan and its one run record");
        await using var ctx = _factory.CreateDbContext();
        (await ctx.PlanRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task APlanRetentionDeletes_GoesWithItsRunRecords()
    {
        var plan = await SaveAsync("alice");
        await _store.RecordRunAsync(plan.Id, "scope", "job", CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(100));

        (await _store.DeleteIdleSinceAsync(_time.GetUtcNow() - TimeSpan.FromDays(90), _ => false, CancellationToken.None))
            .Should().Be(1);

        await using var ctx = _factory.CreateDbContext();
        (await ctx.PlanRuns.CountAsync()).Should().Be(0, "a run record of a deleted plan leads to nothing and is not kept");
    }

    [Fact]
    public void ThePlans_AreErasedWithTheConversations_AfterEveryStoreTheirRunsLeadTo()
    {
        // The coordinator runs this stage only once every earlier store succeeded; erased earlier, a failure in a store the
        // runs lead to could never be retried.
        _store.ErasureStage.Should().Be(ErasureStage.PlannedTasks);
    }

    [Fact]
    public async Task ListRunTracesAsync_FollowsTheSubPlansAPlanNames_UnderTheSameRunScope()
    {
        // A workflow calling a shared plan as a sub-plan: the child's model steps ran as "{workflow}:{childStep}". This also
        // reaches runs made before runs were recorded, through the person's own plan.
        var child = await SaveAsync(null);
        SetCaller("alice");
        var workflow = CreateTestGraph();
        var subPlanStep = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "Call child",
            Type = StepType.SubPlanInvocation,
            Configuration = new SubPlanConfig { ChildPlanId = child.Id },
            RetryPolicy = new RetryPolicy(),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var toolStep = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "Use a tool",
            Type = StepType.ToolUse,
            Configuration = new ToolUseConfig { ToolName = "search" },
            RetryPolicy = new RetryPolicy(),
            Timeout = TimeSpan.FromSeconds(30),
        };
        workflow = workflow with { Steps = [.. workflow.Steps, subPlanStep, toolStep] };
        (await _store.SavePlanAsync(workflow, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var traces = await _store.ListRunTracesAsync("alice", CancellationToken.None);

        var scope = workflow.Id.Value.ToString();
        traces.StepConversationIds.Should().BeEquivalentTo(
            workflow.Steps.Concat(child.Steps).Where(s => s.Type == StepType.LlmCall)
                .Select(s => PlanRunKeys.StepConversationId(scope, s.Id)),
            "only model steps run as conversations; the sub-plan step itself does not");
    }

    [Fact]
    public async Task EraseSubjectAsync_ARunRecordedAfterTheListWasRead_KeepsItsPlanAndRecord()
    {
        // The stores the runs lead to were erased for what was listed. A run made since was not among them; its plan and
        // record stay so the next request finds it. No clock is involved: the listing names the records as read.
        // A kept plan's earlier, finished runs were erased with the rest, so their records go.
        var ranBefore = await SaveAsync("alice");
        var ranSince = await SaveAsync("alice");
        await RunAsync(ranBefore, "scope-before", "job-before");
        await RunAsync(ranSince, "scope-early", "job-early");
        var traces = await _store.ListRunTracesAsync("alice", CancellationToken.None);
        await RunAsync(ranSince, "scope-since", "job-since");

        await _store.EraseSubjectAsync(new ErasureSubject("alice", [], traces), CancellationToken.None);

        await using var ctx = _factory.CreateDbContext();
        (await ctx.PlanGraphs.Select(p => p.Id).ToListAsync()).Should().Equal(ranSince.Id.Value);
        (await ctx.PlanRuns.Select(r => r.ToolResultScopeId).ToListAsync()).Should().Equal("job-since");
        (await _store.ListRunTracesAsync("alice", CancellationToken.None)).IsEmpty.Should().BeFalse(
            "the coordinator reads again and tells the person to run erasure again");
    }

    [Fact]
    public async Task EraseSubjectAsync_ARunStillInProgress_KeepsItsPlanAndRecord_UntilTheMarkIsADayOld()
    {
        // Its steps are still writing under ids whose stores were just erased. A day-old mark is a crashed run.
        var plan = await SaveAsync("alice");
        await _store.RecordRunAsync(plan.Id, "scope", "job", CancellationToken.None);
        var traces = await _store.ListRunTracesAsync("alice", CancellationToken.None);

        await _store.EraseSubjectAsync(new ErasureSubject("alice", [], traces), CancellationToken.None);
        await using (var ctx = _factory.CreateDbContext())
            (await ctx.PlanRuns.CountAsync()).Should().Be(1, "the run is in progress");

        _time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await _store.EraseSubjectAsync(new ErasureSubject("alice", [], traces), CancellationToken.None);
        await using (var ctx = _factory.CreateDbContext())
        {
            (await ctx.PlanRuns.CountAsync()).Should().Be(0, "a mark older than a day is a run whose host stopped");
            (await ctx.PlanGraphs.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task EraseSubjectAsync_ARecordChangedSinceItWasListed_IsKept()
    {
        // Run again under the same scopes after the listing: the same row, but its run may have written since.
        var plan = await SaveAsync("alice");
        await RunAsync(plan, "scope", "job");
        var traces = await _store.ListRunTracesAsync("alice", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));
        await RunAsync(plan, "scope", "job");

        await _store.EraseSubjectAsync(new ErasureSubject("alice", [], traces), CancellationToken.None);

        await using var ctx = _factory.CreateDbContext();
        (await ctx.PlanGraphs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RecordRunEndedAsync_ARunNeverRecorded_IsNotRecordedByItsEnd()
    {
        // A run whose record was erased while it ran must not come back as it ends.
        var plan = await SaveAsync("alice");

        (await _store.RecordRunEndedAsync(plan.Id, "scope", "job", CancellationToken.None)).IsSuccess.Should().BeTrue();

        await using var ctx = _factory.CreateDbContext();
        (await ctx.PlanRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ListRunTracesAsync_ASharedPlanRunUnderItsOwnId_ListsOnlyItsToolScope()
    {
        // Everyone who ran that plan would derive the same step ids, so they are not the person's alone.
        var shared = await SaveAsync(null);
        SetCaller("alice");
        await _store.RecordRunAsync(shared.Id, shared.Id.Value.ToString(), "job-alice", CancellationToken.None);

        var traces = await _store.ListRunTracesAsync("alice", CancellationToken.None);

        traces.StepConversationIds.Should().BeEmpty();
        traces.RunBudgetKeys.Should().BeEmpty();
        traces.ToolResultScopeIds.Should().Equal("job-alice");
    }

    /// <summary>A run of <paramref name="plan"/> by its owner that starts and ends.</summary>
    private async Task RunAsync(PlanGraph plan, string runScope, string toolResultScopeId)
    {
        (await _store.RecordRunAsync(plan.Id, runScope, toolResultScopeId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await _store.RecordRunEndedAsync(plan.Id, runScope, toolResultScopeId, CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RecordRunAsync_APlanTheCallerCannotSee_IsNotRecorded()
    {
        // A run recorded against another person's plan would let one person's erasure reach the other's step sessions.
        var bobs = await SaveAsync("bob");
        SetCaller("alice");

        (await _store.RecordRunAsync(bobs.Id, bobs.Id.Value.ToString(), "job", CancellationToken.None)).IsSuccess
            .Should().BeFalse();

        await using var ctx = _factory.CreateDbContext();
        (await ctx.PlanRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RecordRunAsync_NoCaller_RecordsNothing()
    {
        var plan = await SaveAsync(null);

        (await _store.RecordRunAsync(plan.Id, "scope", "job", CancellationToken.None)).IsSuccess.Should().BeTrue();

        await using var ctx = _factory.CreateDbContext();
        (await ctx.PlanRuns.CountAsync()).Should().Be(0, "no person can ask to be erased from a run nobody made");
    }

    [Fact]
    public async Task RecordRunAsync_TheSameRunAgain_RefreshesItsOneRow()
    {
        var plan = await SaveAsync("alice");
        await _store.RecordRunAsync(plan.Id, "scope", "job", CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(3));

        await _store.RecordRunAsync(plan.Id, "scope", "job", CancellationToken.None);

        await using var ctx = _factory.CreateDbContext();
        var row = await ctx.PlanRuns.SingleAsync();
        row.LastRunAt.Should().Be(Start.AddHours(3));
    }

    [Fact]
    public async Task RecordRunAsync_ToolScopesTheSpillSweepHasOutlived_ArePruned_KeepingTheRunScope()
    {
        // Spills go after 7 days by default; a run's tool scope is kept a day past that.
        var plan = await SaveAsync("alice");
        await _store.RecordRunAsync(plan.Id, "scope", "job-old", CancellationToken.None);
        await _store.RecordRunAsync(plan.Id, "other-scope", "job-other", CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(8) - TimeSpan.FromMinutes(1));
        await _store.RecordRunAsync(plan.Id, "scope", "job-recent", CancellationToken.None);
        (await ToolScopesAsync()).Should().BeEquivalentTo(["job-old", "job-recent", "job-other"],
            "a scope one minute short of the limit is kept");

        _time.Advance(TimeSpan.FromMinutes(2));
        await _store.RecordRunAsync(plan.Id, "scope", "job-new", CancellationToken.None);

        (await ToolScopesAsync()).Should().BeEquivalentTo(["job-recent", "job-new", "job-other"],
            "only the same run scope's outlived tool scope goes; another run scope's row is as old, but it is that scope's "
            + "only record, and is pruned only by that scope's own later runs");
    }

    [Fact]
    public async Task RecordRunAsync_SpillSweepOff_KeepsEveryToolScope()
    {
        // With the sweep off, spills are kept indefinitely, so the ids that lead to them must be too.
        _config.AI.ContextManagement.ToolResultRetention.Enabled = false;
        var plan = await SaveAsync("alice");
        await _store.RecordRunAsync(plan.Id, "scope", "job-old", CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(400));

        await _store.RecordRunAsync(plan.Id, "scope", "job-new", CancellationToken.None);

        (await ToolScopesAsync()).Should().BeEquivalentTo(["job-old", "job-new"]);
    }

    private async Task<string[]> ToolScopesAsync()
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.PlanRuns.Select(r => r.ToolResultScopeId).ToArrayAsync();
    }

    private async Task<PlanGraph> SaveAsync(string? owner)
    {
        SetCaller(owner);
        var graph = CreateTestGraph();
        (await _store.SavePlanAsync(graph, CancellationToken.None)).IsSuccess.Should().BeTrue();
        return graph;
    }

    private void SetCaller(string? owner)
    {
        _scope.UserId = owner;
        _scope.TenantId = owner is null ? null : "acme";
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
            Name = "Run Record Test Plan",
            Steps = steps,
            Edges = [new PlanEdge(steps[0].Id, steps[1].Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { MaxParallelSteps = 4, PlanTimeout = TimeSpan.FromMinutes(10) },
        };
    }

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
