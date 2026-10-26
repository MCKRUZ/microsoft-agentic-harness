using System.Text.Json;
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
/// Personal-data export (#902) for <see cref="EfCorePlanStateStore"/>: the copy holds every plan the person owns with
/// everything recorded under it, and every run they made, and nothing of another owner's plans.
/// </summary>
/// <remarks>
/// Plans store the owner canonicalized, so the subject is built from a differently-cased, space-padded form of the id. A
/// run the person made on a plan they do not own is theirs, but the plan is not: the copy carries the run record alone.
/// </remarks>
public sealed class EfCorePlanStateStoreExportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly StubKnowledgeScope _scope = new();
    private readonly EfCorePlanStateStore _store;

    public EfCorePlanStateStoreExportTests()
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
    public async Task ExportSubjectAsync_OwnedPlan_CarriesItsStepsStatesEdgesLogAndRuns()
    {
        var plan = await SaveRunAsync(" Alice ", "alice plan", "job-alice");
        await SaveRunAsync("bob", "bob plan", "job-bob");

        var records = await _store.ExportSubjectAsync(new ExportSubject("ALICE ", []), CancellationToken.None);

        var exported = records.Should().ContainSingle().Subject;
        exported.GetProperty("id").GetGuid().Should().Be(plan.Id.Value);
        exported.GetProperty("name").GetString().Should().Be("alice plan");
        var steps = exported.GetProperty("steps").EnumerateArray().ToList();
        steps.Should().HaveCount(2);
        steps.Should().Contain(s => s.GetProperty("configurationJson").GetString()!.Contains("Prompt 0"),
            "a model step's prompt is the person's own text");
        steps.Should().Contain(s => s.GetProperty("state").ValueKind == JsonValueKind.Object
            && s.GetProperty("state").GetProperty("output").GetString() == "alice output");
        exported.GetProperty("edges").GetArrayLength().Should().Be(1);
        exported.GetProperty("executionLog").GetArrayLength().Should().BePositive();
        exported.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("toolResultScopeId").GetString())
            .Should().Equal("job-alice");
        string.Join("\n", records.Select(r => r.GetRawText())).Should().NotContain("bob");
    }

    [Fact]
    public async Task ExportSubjectAsync_RunOnAPlanTheyDoNotOwn_IsTheRunRecordAlone()
    {
        SetCaller(null);
        var shared = CreateGraph("shared sub-plan");
        (await _store.SavePlanAsync(shared, CancellationToken.None)).IsSuccess.Should().BeTrue();
        SetCaller("alice");
        (await _store.RecordRunAsync(shared.Id, "scope-1", "job-shared", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var records = await _store.ExportSubjectAsync(new ExportSubject("alice", []), CancellationToken.None);

        var run = records.Should().ContainSingle().Subject.GetProperty("run");
        run.GetProperty("planId").GetGuid().Should().Be(shared.Id.Value);
        run.GetProperty("toolResultScopeId").GetString().Should().Be("job-shared");
        records.Single().GetRawText().Should().NotContain("shared sub-plan", "the plan belongs to its owner, not the person");
    }

    [Fact]
    public async Task ExportSubjectAsync_HoldsExactlyWhatErasureThenDeletes()
    {
        await SaveRunAsync("alice", "first", "job-1");
        await SaveRunAsync("alice", "second", "job-2");
        await SaveRunAsync("bob", "theirs", "job-3");
        SetCaller(null);
        var shared = CreateGraph("shared");
        (await _store.SavePlanAsync(shared, CancellationToken.None)).IsSuccess.Should().BeTrue();
        SetCaller("alice");
        (await _store.RecordRunAsync(shared.Id, "scope-s", "job-s", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var records = await _store.ExportSubjectAsync(new ExportSubject("alice", []), CancellationToken.None);
        var plans = records.Count(r => r.TryGetProperty("steps", out _));
        var runs = records.Sum(r => r.TryGetProperty("runs", out var nested) ? nested.GetArrayLength() : 1);
        var erased = await _store.EraseSubjectAsync(new ErasureSubject("alice", []), CancellationToken.None);

        (plans, runs).Should().Be((2, 3));
        (plans + runs).Should().Be(erased, "the copy must hold the plans and runs an erasure deletes, no more and no less");
    }

    [Fact]
    public async Task ExportSubjectAsync_OwnerWithNothingStored_ReturnsNothing()
    {
        await SaveRunAsync("bob", "theirs", "job-bob");

        (await _store.ExportSubjectAsync(new ExportSubject("nobody", []), CancellationToken.None)).Should().BeEmpty();
    }

    /// <summary>A plan saved by its owner, one step run with output, and a finished run record.</summary>
    private async Task<PlanGraph> SaveRunAsync(string owner, string name, string toolResultScopeId)
    {
        SetCaller(owner);
        var graph = CreateGraph(name);
        (await _store.SavePlanAsync(graph, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await _store.UpdateStepStateAsync(
            new StepExecutionState
            {
                StepId = graph.Steps[0].Id,
                Status = StepExecutionStatus.Completed,
                AttemptCount = 1,
                Output = $"{owner.Trim().ToLowerInvariant()} output",
            },
            CancellationToken.None)).IsSuccess.Should().BeTrue();
        var runScope = graph.Id.Value.ToString();
        (await _store.RecordRunAsync(graph.Id, runScope, toolResultScopeId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await _store.RecordRunEndedAsync(graph.Id, runScope, toolResultScopeId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        return graph;
    }

    private void SetCaller(string? owner)
    {
        _scope.UserId = owner;
        _scope.TenantId = owner is null ? null : "acme";
    }

    private static PlanGraph CreateGraph(string name)
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
            Name = name,
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
