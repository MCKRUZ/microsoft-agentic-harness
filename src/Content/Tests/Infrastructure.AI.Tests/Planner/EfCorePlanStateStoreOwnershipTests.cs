using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.Planner;
using Domain.Common;
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
/// Ownership tests for <see cref="EfCorePlanStateStore"/> (PR W1): schema initialization via
/// the registered <see cref="SchemaInitializer{TContext}"/>, owner/tenant stamping from the
/// ambient <see cref="IKnowledgeScope"/>, and 404-not-403 scope filtering on every
/// read/list/execute path.
/// </summary>
public sealed class EfCorePlanStateStoreOwnershipTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlannerDbContext> _options;
    private readonly TestDbContextFactory _factory;
    private readonly FakeTimeProvider _timeProvider;
    private readonly StubKnowledgeScope _scope;
    private readonly EfCorePlanStateStore _store;

    public EfCorePlanStateStoreOwnershipTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Deliberately NO manual EnsureCreated: schema creation must come from the
        // SchemaInitializer the store demands, proving the production "no such table"
        // hole is closed by the registered lifecycle.
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));
        _factory = new TestDbContextFactory(_options);
        _scope = new StubKnowledgeScope();
        _store = new EfCorePlanStateStore(
            _factory,
            NullLogger<EfCorePlanStateStore>.Instance,
            _timeProvider,
            _scope,
            new PlannerSchemaInitializer(_factory));
    }

    public void Dispose() => _connection.Dispose();

    // --- Schema initialization ---

    [Fact]
    public async Task SavePlanAsync_FreshDatabaseWithSchemaInitializerOnly_Succeeds()
    {
        var graph = CreateTestGraph();

        var result = await _store.SavePlanAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "the SchemaInitializer demanded by the store's constructor must have created the schema");

        var loaded = await _store.LoadPlanAsync(graph.Id, CancellationToken.None);
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Should().NotBeNull();
    }

    // --- Stamping ---

    [Fact]
    public async Task SavePlanAsync_WithAmbientScope_StampsCanonicalOwnerAndTenant()
    {
        _scope.UserId = " Alice ";
        _scope.TenantId = "ACME";
        var graph = CreateTestGraph();

        var result = await _store.SavePlanAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var ctx = _factory.CreateDbContext();
        var entity = await ctx.PlanGraphs.SingleAsync(g => g.Id == graph.Id.Value);
        entity.OwnerId.Should().Be("alice", "identity is canonicalized (trimmed, lowercase) on write");
        entity.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task SavePlanAsync_WithoutAmbientScope_LeavesOwnerAndTenantNull()
    {
        var graph = CreateTestGraph();

        var result = await _store.SavePlanAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var ctx = _factory.CreateDbContext();
        var entity = await ctx.PlanGraphs.SingleAsync(g => g.Id == graph.Id.Value);
        entity.OwnerId.Should().BeNull("no ambient identity means a global (unowned) plan");
        entity.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task SavePlanAsync_DuplicatePlanId_ReturnsGenericFailure()
    {
        var graph = CreateTestGraph();
        (await _store.SavePlanAsync(graph, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await _store.SavePlanAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse("a colliding id must not surface as an unhandled exception");
        result.Errors.Should().ContainSingle()
            .Which.Should().Be("A plan with this id already exists.", "no internals may be echoed");
    }

    [Fact]
    public async Task SavePlanAsync_OversizedScopeIdentity_ReturnsFailure()
    {
        SetCaller(new string('a', 300), "acme");
        var graph = CreateTestGraph();

        var result = await _store.SavePlanAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse(
            "SQLite's HasMaxLength is advisory, so oversized identities must fail cleanly instead of truncating");
    }

    // --- Read/list/execute filtering ---

    [Fact]
    public async Task LoadPlanAsync_DifferentOwnerScope_ReturnsNullLikeMissingPlan()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        SetCaller("bob", "acme");
        var result = await _store.LoadPlanAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull("another owner's plan must be indistinguishable from a missing one");
    }

    [Fact]
    public async Task LoadPlanAsync_DifferentTenantScope_ReturnsNullLikeMissingPlan()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        SetCaller("alice", "globex");
        var result = await _store.LoadPlanAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task LoadPlanAsync_SameOwnerScope_RoundTripsPlan()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        SetCaller("alice", "acme");
        var result = await _store.LoadPlanAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Id.Should().Be(graph.Id);
        result.Value.Steps.Should().HaveCount(graph.Steps.Count);
    }

    [Fact]
    public async Task LoadPlanAsync_GlobalPlanWithScopedCaller_ReturnsPlan()
    {
        var graph = CreateTestGraph();
        await _store.SavePlanAsync(graph, CancellationToken.None); // unscoped save = global plan

        SetCaller("alice", "acme");
        var result = await _store.LoadPlanAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull("a null-owner/null-tenant plan is global and visible to every caller");
    }

    [Fact]
    public async Task ListPlansAsync_DifferentOwnerScope_ExcludesOtherOwnersPlans()
    {
        var alicePlan = await SaveAsOwnerAsync("alice", "acme");
        SetCaller("bob", "acme");
        var bobPlan = CreateTestGraph();
        await _store.SavePlanAsync(bobPlan, CancellationToken.None);

        var result = await _store.ListPlansAsync(null, null, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(p => p.Id).Should().Contain(bobPlan.Id)
            .And.NotContain(alicePlan.Id, "listing must never surface another owner's plans");
    }

    [Fact]
    public async Task LoadStepStatesAsync_DifferentOwnerScope_ReturnsEmptyLikeMissingPlan()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        SetCaller("bob", "acme");
        var result = await _store.LoadStepStatesAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetExecutionHistoryAsync_DifferentOwnerScope_ReturnsEmptyLikeMissingPlan()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        SetCaller("bob", "acme");
        var result = await _store.GetExecutionHistoryAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeAsync_DifferentOwnerScope_ReturnsNotFound()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        SetCaller("bob", "acme");
        var result = await _store.ResumeAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound,
            "cross-owner access must read as NotFound, never Forbidden");
    }

    [Fact]
    public async Task UpdateStepStateAsync_DifferentOwnerScope_ReturnsNotFound()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        SetCaller("bob", "acme");
        var state = new StepExecutionState
        {
            StepId = graph.Steps[0].Id,
            Status = StepExecutionStatus.Running,
            AttemptCount = 1,
            StartedAt = _timeProvider.GetUtcNow(),
        };
        var result = await _store.UpdateStepStateAsync(state, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task CheckpointAsync_DifferentOwnerScope_ReturnsNotFound()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        SetCaller("bob", "acme");
        var states = graph.Steps.Select(s => new StepExecutionState
        {
            StepId = s.Id,
            Status = StepExecutionStatus.Completed,
            AttemptCount = 1,
        }).ToList();
        var result = await _store.CheckpointAsync(graph.Id, states, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    // --- Global plans: readable by all, mutable only from a null-identity (system) context ---

    [Fact]
    public async Task UpdateStepStateAsync_GlobalPlanWithScopedCaller_ReturnsNotFound()
    {
        var graph = CreateTestGraph();
        await _store.SavePlanAsync(graph, CancellationToken.None); // unscoped save = global plan

        SetCaller("alice", "acme");
        var state = new StepExecutionState
        {
            StepId = graph.Steps[0].Id,
            Status = StepExecutionStatus.Running,
            AttemptCount = 1,
        };
        var result = await _store.UpdateStepStateAsync(state, CancellationToken.None);

        result.IsSuccess.Should().BeFalse("shared visibility must never grant shared mutation");
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task CheckpointAsync_GlobalPlanWithScopedCaller_ReturnsNotFound()
    {
        var graph = CreateTestGraph();
        await _store.SavePlanAsync(graph, CancellationToken.None);

        SetCaller("alice", "acme");
        var states = graph.Steps.Select(s => new StepExecutionState
        {
            StepId = s.Id,
            Status = StepExecutionStatus.Completed,
            AttemptCount = 1,
        }).ToList();
        var result = await _store.CheckpointAsync(graph.Id, states, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task ResumeAsync_GlobalPlanWithScopedCaller_ReturnsNotFound()
    {
        var graph = CreateTestGraph();
        await _store.SavePlanAsync(graph, CancellationToken.None);

        SetCaller("alice", "acme");
        var result = await _store.ResumeAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task ResumeAsync_GlobalPlanWithNullScopeCaller_Succeeds()
    {
        var graph = CreateTestGraph();
        await _store.SavePlanAsync(graph, CancellationToken.None);

        var result = await _store.ResumeAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a null-identity caller owns global (null-stamped) plans");
        result.Value.Should().HaveCount(graph.Steps.Count);
    }

    [Fact]
    public async Task UpdateStepStateAsync_SameOwnerScope_Succeeds()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        var state = new StepExecutionState
        {
            StepId = graph.Steps[0].Id,
            Status = StepExecutionStatus.Running,
            AttemptCount = 1,
            StartedAt = _timeProvider.GetUtcNow(),
        };
        var result = await _store.UpdateStepStateAsync(state, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("owners must retain full mutation rights over their own plans");
    }

    [Fact]
    public async Task ResumeAsync_SameOwnerScope_Succeeds()
    {
        var graph = await SaveAsOwnerAsync("alice", "acme");

        var result = await _store.ResumeAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(graph.Steps.Count);
    }

    // --- Helpers ---

    private void SetCaller(string? userId, string? tenantId)
    {
        _scope.UserId = userId;
        _scope.TenantId = tenantId;
    }

    private async Task<PlanGraph> SaveAsOwnerAsync(string userId, string tenantId)
    {
        SetCaller(userId, tenantId);
        var graph = CreateTestGraph();
        var saved = await _store.SavePlanAsync(graph, CancellationToken.None);
        saved.IsSuccess.Should().BeTrue();
        return graph;
    }

    // --- Storage quota counting ---

    [Fact]
    public async Task CountOwnedPlansAsync_CountsOnlyWhatTheCallerOwns()
    {
        _scope.UserId = "alice";
        _scope.TenantId = "acme";
        await _store.SavePlanAsync(CreateTestGraph(), CancellationToken.None);
        await _store.SavePlanAsync(CreateTestGraph(), CancellationToken.None);

        _scope.UserId = "mallory";
        await _store.SavePlanAsync(CreateTestGraph(), CancellationToken.None);

        var mallorysCount = await _store.CountOwnedPlansAsync(CancellationToken.None);
        mallorysCount.Value.Should().Be(1, "a quota must charge each caller only for its own records");

        _scope.UserId = "alice";
        var alicesCount = await _store.CountOwnedPlansAsync(CancellationToken.None);
        alicesCount.Value.Should().Be(2);
    }

    [Fact]
    public async Task CountOwnedPlansAsync_DoesNotChargeTheCallerForGloballyReadablePlans()
    {
        // A null-owner plan is readable by everyone and belongs to no one. Counting visible plans
        // rather than owned ones would charge every caller for records none of them created, so one
        // shared record could exhaust every caller's quota at once.
        _scope.UserId = null;
        _scope.TenantId = null;
        await _store.SavePlanAsync(CreateTestGraph(), CancellationToken.None);

        _scope.UserId = "alice";
        _scope.TenantId = "acme";

        var count = await _store.CountOwnedPlansAsync(CancellationToken.None);

        count.IsSuccess.Should().BeTrue();
        count.Value.Should().Be(0);
    }

    private static PlanGraph CreateTestGraph()
    {
        var steps = Enumerable.Range(0, 2).Select(i => new PlanStep
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

        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "Ownership Test Plan",
            Steps = steps,
            Edges = [new PlanEdge(steps[0].Id, steps[1].Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration
            {
                MaxParallelSteps = 4,
                PlanTimeout = TimeSpan.FromMinutes(10),
            },
        };
    }

    /// <summary>Mutable <see cref="IKnowledgeScope"/> stub simulating different ambient callers.</summary>
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
