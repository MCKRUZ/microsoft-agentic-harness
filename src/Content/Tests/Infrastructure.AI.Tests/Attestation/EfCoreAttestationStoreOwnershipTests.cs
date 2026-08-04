using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.Attestation;
using Domain.AI.Planner;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.Attestation;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Attestation;

/// <summary>
/// Ownership tests for <see cref="EfCoreAttestationStore"/>: attestations inherit their
/// plan's scope boundaries via the shared <c>PlannerScopeFilter</c> — reads gated by
/// visibility, saves by strict writability, cross-owner access indistinguishable from
/// absence (404-not-403).
/// </summary>
public sealed class EfCoreAttestationStoreOwnershipTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly StubKnowledgeScope _scope;
    private readonly EfCorePlanStateStore _planStore;
    private readonly EfCoreAttestationStore _attestationStore;

    public EfCoreAttestationStoreOwnershipTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        _scope = new StubKnowledgeScope();

        _planStore = new EfCorePlanStateStore(
            _factory,
            NullLogger<EfCorePlanStateStore>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero)),
            _scope,
            new SchemaInitializer<PlannerDbContext>(_factory));

        _attestationStore = new EfCoreAttestationStore(
            _factory,
            NullLogger<EfCoreAttestationStore>.Instance,
            _scope);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SaveAsync_SameOwnerScope_PersistsAttestation()
    {
        var graph = await SavePlanAsOwnerAsync("alice", "acme");

        var save = await _attestationStore.SaveAsync(
            graph.Steps[0].Id, CreateAttestation(), CancellationToken.None);
        var read = await _attestationStore.GetByStepAsync(graph.Steps[0].Id, CancellationToken.None);

        save.IsSuccess.Should().BeTrue();
        read.IsSuccess.Should().BeTrue();
        read.Value.Should().NotBeNull("the owner must round-trip their own attestations");
    }

    [Fact]
    public async Task SaveAsync_DifferentOwnerScope_ReturnsNotFound()
    {
        var graph = await SavePlanAsOwnerAsync("alice", "acme");

        SetCaller("bob", "acme");
        var result = await _attestationStore.SaveAsync(
            graph.Steps[0].Id, CreateAttestation(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound,
            "cross-owner writes must read as NotFound, never Forbidden");
    }

    [Fact]
    public async Task SaveAsync_GlobalPlanWithScopedCaller_ReturnsNotFound()
    {
        var graph = await SavePlanAsOwnerAsync(null, null); // global plan

        SetCaller("alice", "acme");
        var result = await _attestationStore.SaveAsync(
            graph.Steps[0].Id, CreateAttestation(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse("shared visibility must never grant shared mutation");
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task GetByStepAsync_DifferentOwnerScope_ReturnsNullLikeMissing()
    {
        var graph = await SavePlanAsOwnerAsync("alice", "acme");
        (await _attestationStore.SaveAsync(
            graph.Steps[0].Id, CreateAttestation(), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        SetCaller("bob", "acme");
        var result = await _attestationStore.GetByStepAsync(graph.Steps[0].Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull("another owner's attestation must be indistinguishable from a missing one");
    }

    [Fact]
    public async Task GetByPlanAsync_DifferentOwnerScope_ReturnsEmptyLikeMissing()
    {
        var graph = await SavePlanAsOwnerAsync("alice", "acme");
        (await _attestationStore.SaveAsync(
            graph.Steps[0].Id, CreateAttestation(), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        SetCaller("bob", "acme");
        var result = await _attestationStore.GetByPlanAsync(graph.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    // --- Helpers ---

    private void SetCaller(string? userId, string? tenantId)
    {
        _scope.UserId = userId;
        _scope.TenantId = tenantId;
    }

    private async Task<PlanGraph> SavePlanAsOwnerAsync(string? userId, string? tenantId)
    {
        SetCaller(userId, tenantId);
        var graph = CreateTestGraph();
        var saved = await _planStore.SavePlanAsync(graph, CancellationToken.None);
        saved.IsSuccess.Should().BeTrue();
        return graph;
    }

    private static ToolExecutionAttestation CreateAttestation() => new()
    {
        ToolName = "file_system",
        InputHash = "input-hash",
        OutputHash = "output-hash",
        Timestamp = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero),
        Signature = "signature",
        KeyVersion = "v1",
    };

    private static PlanGraph CreateTestGraph()
    {
        var step = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "Step 0",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig
            {
                SystemPrompt = "Prompt",
                ModelDeploymentKey = "gpt-4o",
            },
            RetryPolicy = new RetryPolicy { MaxRetries = 2 },
            Timeout = TimeSpan.FromSeconds(30),
        };

        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "Attestation Ownership Test Plan",
            Steps = [step],
            Edges = [],
            Configuration = new PlanConfiguration
            {
                MaxParallelSteps = 1,
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
