using Infrastructure.Postgres.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// The runner's own guarantees: apply once, apply in order, never half-apply, and stay correct when
/// two hosts start at the same moment.
/// </summary>
public sealed class PostgresMigrationRunnerTests
{
    [SkippableFact]
    public async Task ARunAgainstAnUpToDateDatabase_AppliesNothing()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();
        var scripts = Scripts(("001_a", "CREATE TABLE a (id INT)"), ("002_b", "CREATE TABLE b (id INT)"));

        Assert.Equal(2, await ApplyAsync(schema, scripts));
        Assert.Equal(0, await ApplyAsync(schema, scripts));
        Assert.Equal(2, await schema.ScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations"));
    }

    [SkippableFact]
    public async Task OnlyTheScriptsMissingFromTheLedgerAreApplied()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        await ApplyAsync(schema, Scripts(("001_a", "CREATE TABLE a (id INT)")));

        var applied = await ApplyAsync(schema, Scripts(
            ("001_a", "SELECT 1/0"), // would throw if re-run; proves it is skipped, not merely tolerated
            ("002_b", "CREATE TABLE b (id INT)")));

        Assert.Equal(1, applied);
        Assert.Equal(1, await CountTablesAsync(schema, "b"));
    }

    [SkippableFact]
    public async Task ScriptsAreAppliedInOrdinalOrderRegardlessOfTheOrderTheyWereHandedOver()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        // 002 depends on 001. Handed over backwards, so only the sort can save it.
        var outOfOrder = new[]
        {
            new MigrationScript(2, "002_add_column", "ALTER TABLE ordered ADD COLUMN label TEXT"),
            new MigrationScript(1, "001_create_table", "CREATE TABLE ordered (id INT)"),
        };

        await ApplyAsync(schema, outOfOrder);

        Assert.Equal(1, await schema.ScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.columns " +
            $"WHERE table_schema = '{schema.SchemaName}' AND table_name = 'ordered' AND column_name = 'label'"));
    }

    [SkippableFact]
    public async Task AFailingMigrationThrows_AndLeavesTheDatabaseAtItsPreviousVersion()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var ex = await Assert.ThrowsAsync<PostgresMigrationException>(() => ApplyAsync(schema, Scripts(
            ("001_good", "CREATE TABLE survivor (id INT)"),
            ("002_bad", "CREATE TABLE nonsense (id NOT_A_TYPE)"))));

        Assert.Equal("002_bad", ex.MigrationId);

        // All-or-nothing: 001 succeeded inside the transaction and is rolled back with 002. A
        // half-applied schema is a state nothing else in this system knows how to reason about,
        // whereas "still on the previous version" is the state it was already in.
        Assert.Equal(0, await CountTablesAsync(schema, "survivor"));
        Assert.Equal(0, await CountTablesAsync(schema, "nonsense"));
        Assert.Equal(0, await CountTablesAsync(schema, "schema_migrations"));
    }

    [SkippableFact]
    public async Task TwoHostsStartingTogether_ApplyEveryMigrationExactlyOnce()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        // The only real proof of the advisory lock. Without it both runners read an empty ledger,
        // both decide everything is outstanding, and the second one re-runs DDL the first already
        // committed — which on a CREATE TABLE without IF NOT EXISTS is an outright failure and on a
        // seed INSERT is silent duplication.
        var scripts = Scripts(
            ("001_a", "CREATE TABLE raced (id INT PRIMARY KEY)"),
            ("002_b", "INSERT INTO raced (id) VALUES (1)"));

        var results = await Task.WhenAll(
            Task.Run(() => ApplyAsync(schema, scripts)),
            Task.Run(() => ApplyAsync(schema, scripts)));

        // One runner did all the work; the other found nothing to do. Which one is not determined.
        Assert.Equal([0, 2], results.OrderBy(r => r));
        Assert.Equal(2, await schema.ScalarAsync<int>("SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(1, await schema.ScalarAsync<int>("SELECT COUNT(*) FROM raced"));
    }

    [SkippableFact]
    public async Task TheLedgerRecordsWhatWasAppliedAndWhen()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();
        await ApplyAsync(schema, Scripts(("001_a", "CREATE TABLE a (id INT)")));

        Assert.Equal("001_a", await schema.ScalarAsync<string>("SELECT id FROM schema_migrations"));
        Assert.Equal(0, await schema.ScalarAsync<int>(
            "SELECT COUNT(*) FROM schema_migrations WHERE applied_at IS NULL"));
    }

    private static async Task<int> ApplyAsync(
        MigrationTestSchema schema, IReadOnlyList<MigrationScript> scripts)
    {
        var runner = new PostgresMigrationRunner(
            new PostgresMigrationOptions("schema_migrations", schema.AdvisoryLockKey),
            scripts,
            NullLogger.Instance);

        await using var connection = await schema.OpenAsync();
        return await runner.ApplyAsync(connection);
    }

    private static Task<int> CountTablesAsync(MigrationTestSchema schema, string tableName) =>
        schema.ScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.tables " +
            $"WHERE table_schema = '{schema.SchemaName}' AND table_name = '{tableName}'");

    private static MigrationScript[] Scripts(params (string Id, string Sql)[] scripts) =>
        scripts.Select((s, i) => new MigrationScript(i + 1, s.Id, s.Sql)).ToArray();
}
