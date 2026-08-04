using Application.AI.Common.Services.KnowledgeGraph;
using Domain.AI.Planner;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Infrastructure.AI.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Tests <see cref="SchemaInitializer{TContext}"/> against the planner's schema: evolution of a
/// pre-existing (legacy) planner database that lacks the ownership columns — the case EnsureCreated
/// alone can never fix because it no-ops on existing databases.
/// </summary>
/// <remarks>
/// These assertions predate the generic reconciler: they were written for a hand-rolled
/// <c>PlannerSchemaInitializer</c> that added <c>OwnerId</c>/<c>TenantId</c> and their composite
/// index with literal DDL. That subclass is gone, and the assertions are deliberately unchanged —
/// they are the evidence that the model-driven reconciler delivers exactly what the hand-written
/// version did on the one schema whose evolution was already proven.
/// </remarks>
public sealed class PlannerSchemaInitializerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlannerDbContext> _options;
    private readonly TestDbContextFactory<PlannerDbContext> _factory;

    public PlannerSchemaInitializerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory<PlannerDbContext>(_options);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Initialize_LegacyDatabaseWithoutOwnershipColumns_AddsColumns()
    {
        await CreateLegacySchemaAsync();

        _ = new SchemaInitializer<PlannerDbContext>(_factory);

        var columns = await ReadPlanGraphColumnsAsync();
        columns.Should().Contain("OwnerId", "the initializer must evolve pre-existing databases in place")
            .And.Contain("TenantId");
    }

    [Fact]
    public async Task Initialize_LegacyDatabase_ScopeFilteredQueriesWorkAfterwards()
    {
        await CreateLegacySchemaAsync();

        var store = new EfCorePlanStateStore(
            _factory,
            NullLogger<EfCorePlanStateStore>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero)),
            new NullKnowledgeScope(),
            new SchemaInitializer<PlannerDbContext>(_factory));

        var graph = CreateTestGraph();
        var saved = await store.SavePlanAsync(graph, CancellationToken.None);
        saved.IsSuccess.Should().BeTrue();

        // LoadPlanAsync exercises the ownership-column visibility predicate — this is what
        // threw "no such column" on a legacy database before the evolution step.
        var loaded = await store.LoadPlanAsync(graph.Id, CancellationToken.None);
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Should().NotBeNull();
    }

    /// <summary>
    /// The legacy setup drops the composite scope index along with the columns, and the hand-rolled
    /// initializer restored it with literal <c>CREATE INDEX IF NOT EXISTS</c> DDL. Nothing asserted
    /// that until now: every column assertion passed while the index stayed missing, which would have
    /// left scope-filtered queries correct but unindexed — a silent performance cliff rather than a
    /// visible failure.
    /// </summary>
    [Fact]
    public async Task Initialize_LegacyDatabaseWithoutOwnershipColumns_RestoresTheScopeIndex()
    {
        await CreateLegacySchemaAsync();

        _ = new SchemaInitializer<PlannerDbContext>(_factory);

        var indexes = await ReadPlanGraphIndexNamesAsync();
        indexes.Should().Contain(
            "IX_PlanGraphs_TenantId_OwnerId",
            "an index over a just-added column is exactly what reconciliation must restore");
    }

    [Fact]
    public async Task Initialize_RunTwice_IsIdempotent()
    {
        await CreateLegacySchemaAsync();

        _ = new SchemaInitializer<PlannerDbContext>(_factory);
        var second = () => new SchemaInitializer<PlannerDbContext>(_factory);

        second.Should().NotThrow("a column already present is skipped and the index uses IF NOT EXISTS");
    }

    [Fact]
    public void Initialize_FreshDatabase_CreatesFullSchemaIncludingOwnership()
    {
        _ = new SchemaInitializer<PlannerDbContext>(_factory);

        var columns = ReadPlanGraphColumnsAsync().GetAwaiter().GetResult();
        columns.Should().Contain("OwnerId").And.Contain("TenantId");
    }

    /// <summary>
    /// Creates the CURRENT schema, then strips the ownership columns and their composite
    /// index to reproduce a database created before PR W1 shipped.
    /// </summary>
    private async Task CreateLegacySchemaAsync()
    {
        await using var ctx = new PlannerDbContext(_options);
        await ctx.Database.EnsureCreatedAsync();
        await ctx.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_PlanGraphs_TenantId_OwnerId\";");
        await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE \"PlanGraphs\" DROP COLUMN \"OwnerId\";");
        await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE \"PlanGraphs\" DROP COLUMN \"TenantId\";");
    }

    private Task<List<string>> ReadPlanGraphIndexNamesAsync() =>
        SqliteSchemaProbe.IndexNamesAsync(_connection, "PlanGraphs");

    private Task<List<string>> ReadPlanGraphColumnsAsync() =>
        SqliteSchemaProbe.ColumnsAsync(_connection, "PlanGraphs");

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
            Name = "Schema Evolution Test Plan",
            Steps = [step],
            Edges = [],
            Configuration = new PlanConfiguration
            {
                MaxParallelSteps = 1,
                PlanTimeout = TimeSpan.FromMinutes(10),
            },
        };
    }
}
