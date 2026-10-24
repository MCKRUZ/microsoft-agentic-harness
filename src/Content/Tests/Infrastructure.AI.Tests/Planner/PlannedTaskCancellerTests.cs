using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
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
/// <see cref="PlannedTaskCanceller"/> (#907). A planned task still running when its owner's erasure starts keeps writing
/// under the ids erasure reads and clears; stopping it first closes that. Stopping anyone else's task is never acceptable.
/// </summary>
public sealed class PlannedTaskCancellerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly StubKnowledgeScope _scope = new();
    private readonly FakeTimeProvider _storeTime = new(DateTimeOffset.UtcNow);
    private readonly EfCorePlanStateStore _store;
    private readonly PlanRunCancellationRegistry _registry = new();

    public PlannedTaskCancellerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(_connection).Options);
        _store = new EfCorePlanStateStore(
            _factory, NullLogger<EfCorePlanStateStore>.Instance, _storeTime, _scope,
            new SchemaInitializer<PlannerDbContext>(_factory), PlannerTestConfig.Monitor(new AppConfig()));
    }

    public void Dispose() => _connection.Dispose();

    private PlannedTaskCanceller Create(TimeSpan wait) =>
        new(_factory, _registry, TimeProvider.System, NullLogger<PlannedTaskCanceller>.Instance, wait);

    [Fact]
    public async Task ThePersonsRunningTask_IsSignalled_AndOnceItEnds_NothingIsStillRunning()
    {
        var (plan, run) = await StartRunAsync("alice", endsWhenCancelled: true);

        var stillRunning = await Create(TimeSpan.FromSeconds(10)).StopRunningTasksAsync(" Alice ", CancellationToken.None);

        run.Token.IsCancellationRequested.Should().BeTrue();
        stillRunning.Should().Be(0, "the run marked itself ended once it stopped");
    }

    [Fact]
    public async Task AnotherPersonsRunningTask_IsNeverSignalled()
    {
        var (_, bobsRun) = await StartRunAsync("bob", endsWhenCancelled: true);

        (await Create(TimeSpan.FromSeconds(1)).StopRunningTasksAsync("alice", CancellationToken.None)).Should().Be(0);

        bobsRun.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task ARunThatDoesNotStopInTime_IsReportedAsStillRunning()
    {
        // Its plan is then kept by the plan store's erasure, and the receipt says to run erasure again.
        var (_, run) = await StartRunAsync("alice", endsWhenCancelled: false);

        var stillRunning = await Create(TimeSpan.FromMilliseconds(600)).StopRunningTasksAsync("alice", CancellationToken.None);

        run.Token.IsCancellationRequested.Should().BeTrue();
        stillRunning.Should().Be(1);
    }

    [Fact]
    public async Task ARunMarkOlderThanADay_BelongsToACrashedRun_AndIsNotWaitedFor()
    {
        SetCaller("alice");
        var plan = await SaveAsync();
        (await _store.RecordRunAsync(plan.Id, plan.Id.Value.ToString(), "job-1", CancellationToken.None)).IsSuccess.Should().BeTrue();
        await using (var ctx = _factory.CreateDbContext())
        {
            await ctx.PlanRuns.ExecuteUpdateAsync(s => s.SetProperty(r => r.LastRunAt, DateTimeOffset.UtcNow.AddDays(-2)));
        }

        (await Create(TimeSpan.FromSeconds(10)).StopRunningTasksAsync("alice", CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task NoRunningTask_ReturnsAtOnce()
    {
        (await Create(TimeSpan.FromSeconds(30)).StopRunningTasksAsync("alice", CancellationToken.None)).Should().Be(0);
    }

    private async Task<(PlanGraph Plan, PlanRunCancellationRegistration Run)> StartRunAsync(string owner, bool endsWhenCancelled)
    {
        SetCaller(owner);
        var plan = await SaveAsync();
        var scope = plan.Id.Value.ToString();
        (await _store.RecordRunAsync(plan.Id, scope, $"job-{owner}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var run = _registry.Register(plan.Id);
        if (endsWhenCancelled)
        {
            // What the executor does in its finally once the cancelled run unwinds.
            run.Token.Register(() =>
            {
                SetCaller(owner);
                _store.RecordRunEndedAsync(plan.Id, scope, $"job-{owner}", CancellationToken.None).GetAwaiter().GetResult();
            });
        }

        return (plan, run);
    }

    private void SetCaller(string owner)
    {
        _scope.UserId = owner;
        _scope.TenantId = "acme";
    }

    private async Task<PlanGraph> SaveAsync()
    {
        var steps = new List<PlanStep>
        {
            new()
            {
                Id = PlanStepId.New(),
                Name = "Step",
                Type = StepType.LlmCall,
                Configuration = new LlmCallConfig { SystemPrompt = "Prompt", ModelDeploymentKey = "gpt-4o" },
                RetryPolicy = new RetryPolicy { MaxRetries = 0 },
                Timeout = TimeSpan.FromSeconds(30),
            },
        };
        var graph = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "Canceller Test Plan",
            Steps = steps,
            Edges = [],
            Configuration = new PlanConfiguration { MaxParallelSteps = 1, PlanTimeout = TimeSpan.FromMinutes(10) },
        };
        (await _store.SavePlanAsync(graph, CancellationToken.None)).IsSuccess.Should().BeTrue();
        return graph;
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
