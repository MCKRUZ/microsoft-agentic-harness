using Application.AI.Common.Services.KnowledgeGraph;
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
/// Tests for <see cref="PlannerSchemaInitializer"/>: schema evolution on a pre-existing
/// (legacy) planner database that lacks the ownership columns — the case EnsureCreated
/// alone can never fix because it no-ops on existing databases.
/// </summary>
public sealed class PlannerSchemaInitializerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlannerDbContext> _options;
    private readonly TestDbContextFactory _factory;

    public PlannerSchemaInitializerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(_options);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Initialize_LegacyDatabaseWithoutOwnershipColumns_AddsColumns()
    {
        await CreateLegacySchemaAsync();

        _ = new PlannerSchemaInitializer(_factory);

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
            new PlannerSchemaInitializer(_factory));

        var graph = CreateTestGraph();
        var saved = await store.SavePlanAsync(graph, CancellationToken.None);
        saved.IsSuccess.Should().BeTrue();

        // LoadPlanAsync exercises the ownership-column visibility predicate — this is what
        // threw "no such column" on a legacy database before the evolution step.
        var loaded = await store.LoadPlanAsync(graph.Id, CancellationToken.None);
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Initialize_RunTwice_IsIdempotent()
    {
        await CreateLegacySchemaAsync();

        _ = new PlannerSchemaInitializer(_factory);
        var second = () => new PlannerSchemaInitializer(_factory);

        second.Should().NotThrow("the PRAGMA guard and IF NOT EXISTS make re-runs no-ops");
    }

    [Fact]
    public void Initialize_FreshDatabase_CreatesFullSchemaIncludingOwnership()
    {
        _ = new PlannerSchemaInitializer(_factory);

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

    private async Task<List<string>> ReadPlanGraphColumnsAsync()
    {
        var columns = new List<string>();
        await using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"PlanGraphs\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

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

    private sealed class TestDbContextFactory(DbContextOptions<PlannerDbContext> options)
        : IDbContextFactory<PlannerDbContext>
    {
        public PlannerDbContext CreateDbContext() => new(options);
    }
}
