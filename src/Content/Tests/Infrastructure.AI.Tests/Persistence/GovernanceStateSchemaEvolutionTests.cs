using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.AI.Tests.Persistence;

/// <summary>
/// Schema evolution for the governance-state database: a pre-existing file that predates the
/// outcome-seal columns must gain them, across <em>both</em> sealed tables.
/// </summary>
/// <remarks>
/// <para>
/// This case was previously handled by a hand-rolled <c>GovernanceStateSchemaInitializer</c> and
/// covered by no test at all — the planner had the only measured evolution path. Deleting that
/// subclass in favour of the model-driven reconciler would otherwise have moved the governance
/// database from "untested and hand-written" to "untested and inferred", which is not an improvement
/// anyone could verify.
/// </para>
/// <para>
/// Two tables matter here, not one, and that is the point of the second assertion. The deleted
/// initializer read <c>escalation_state</c> first and returned from the whole method when it was
/// absent, so a database carrying only <c>change_proposal</c> silently skipped its seal column. The
/// reconciler walks every table independently.
/// </para>
/// </remarks>
public sealed class GovernanceStateSchemaEvolutionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<GovernanceStateDbContext> _options;
    private readonly TestDbContextFactory<GovernanceStateDbContext> _factory;

    public GovernanceStateSchemaEvolutionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<GovernanceStateDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory<GovernanceStateDbContext>(_options);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Initialize_LegacyDatabaseWithoutSealColumns_AddsThemToBothSealedTables()
    {
        await CreateLegacySchemaAsync();

        _ = new SchemaInitializer<GovernanceStateDbContext>(_factory);

        (await ColumnsOfAsync("escalation_state")).Should().Contain(
            "OutcomeSealJson",
            "an escalation database written before sealing shipped must gain the column in place");

        (await ColumnsOfAsync("change_proposal")).Should().Contain(
            "ProposalSealJson",
            "the second sealed table must be reconciled independently of the first");
    }

    [Fact]
    public async Task Initialize_LegacyDatabaseWithoutSealColumns_RestoresTheStatusIndex()
    {
        await CreateLegacySchemaAsync();

        _ = new SchemaInitializer<GovernanceStateDbContext>(_factory);

        (await IndexNamesOfAsync("escalation_state")).Should().Contain(
            "ix_escalation_state_status_updated_at");
    }

    [Fact]
    public async Task Initialize_RunTwice_IsIdempotent()
    {
        await CreateLegacySchemaAsync();

        _ = new SchemaInitializer<GovernanceStateDbContext>(_factory);
        var second = () => new SchemaInitializer<GovernanceStateDbContext>(_factory);

        second.Should().NotThrow("a column already present is skipped and the index uses IF NOT EXISTS");
    }

    /// <summary>
    /// Builds the current schema, then strips the seal columns and the status index to reproduce a
    /// database created before sealing shipped.
    /// </summary>
    private async Task CreateLegacySchemaAsync()
    {
        await using var context = new GovernanceStateDbContext(_options);
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync("DROP INDEX \"ix_escalation_state_status_updated_at\";");
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"escalation_state\" DROP COLUMN \"OutcomeSealJson\";");
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"change_proposal\" DROP COLUMN \"ProposalSealJson\";");
    }

    private Task<List<string>> ColumnsOfAsync(string table) =>
        SqliteSchemaProbe.ColumnsAsync(_connection, table);

    private Task<List<string>> IndexNamesOfAsync(string table) =>
        SqliteSchemaProbe.IndexNamesAsync(_connection, table);
}
