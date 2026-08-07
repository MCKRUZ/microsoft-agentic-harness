using Infrastructure.Postgres.Migrations;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// The runner's own guarantees: apply once, apply in order, never half-apply, and stay correct when
/// two hosts start at the same moment.
/// </summary>
public sealed class PostgresMigrationRunnerTests
{
    private const string Ledger = "test_schema_migrations";

    [SkippableFact]
    public async Task ARunAgainstAnUpToDateDatabase_AppliesNothing()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();
        var scripts = Scripts(("001_a", "CREATE TABLE a (id INT)"), ("002_b", "CREATE TABLE b (id INT)"));

        Assert.Equal(2, await schema.ApplyAsync(scripts));
        Assert.Equal(0, await schema.ApplyAsync(scripts));
        Assert.Equal(2, await schema.ScalarAsync<int>($"SELECT COUNT(*) FROM {Ledger}"));
    }

    [SkippableFact]
    public async Task OnlyTheScriptsMissingFromTheLedgerAreApplied()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        await schema.ApplyAsync(Scripts(("001_a", "CREATE TABLE a (id INT)")));

        var applied = await schema.ApplyAsync(Scripts(
            ("001_a", "SELECT 1/0"), // would throw if re-run; proves it is skipped, not merely tolerated
            ("002_b", "CREATE TABLE b (id INT)")));

        Assert.Equal(1, applied);
        Assert.Equal(1, await schema.CountTablesAsync("b"));
    }

    [SkippableFact]
    public async Task ScriptsAreAppliedInOrdinalOrderRegardlessOfTheOrderTheyWereHandedOver()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        // 002 depends on 001. Handed over backwards, so only the sort can save it.
        var outOfOrder = new[]
        {
            new MigrationScript("002_add_column", "ALTER TABLE ordered ADD COLUMN label TEXT"),
            new MigrationScript("001_create_table", "CREATE TABLE ordered (id INT)"),
        };

        await schema.ApplyAsync(outOfOrder);

        Assert.Equal(1, await schema.CountColumnsAsync("ordered", "label"));
    }

    [SkippableFact]
    public async Task AFailingMigrationThrows_AndLeavesTheDatabaseAtItsPreviousVersion()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var ex = await Assert.ThrowsAsync<PostgresMigrationException>(() => schema.ApplyAsync(Scripts(
            ("001_good", "CREATE TABLE survivor (id INT)"),
            ("002_bad", "CREATE TABLE nonsense (id NOT_A_TYPE)"))));

        Assert.Equal("002_bad", ex.MigrationId);

        // All-or-nothing: 001 succeeded inside the transaction and is rolled back with 002. A
        // half-applied schema is a state nothing else in this system knows how to reason about,
        // whereas "still on the previous version" is the state it was already in.
        Assert.Equal(0, await schema.CountTablesAsync("survivor"));
        Assert.Equal(0, await schema.CountTablesAsync("nonsense"));
        Assert.Equal(0, await schema.CountTablesAsync(Ledger));
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
            Task.Run(() => schema.ApplyAsync(scripts)),
            Task.Run(() => schema.ApplyAsync(scripts)));

        // One runner did all the work; the other found nothing to do. Which one is not determined.
        Assert.Equal([0, 2], results.OrderBy(r => r));
        Assert.Equal(2, await schema.ScalarAsync<int>($"SELECT COUNT(*) FROM {Ledger}"));
        Assert.Equal(1, await schema.ScalarAsync<int>("SELECT COUNT(*) FROM raced"));
    }

    [SkippableFact]
    public async Task TheLedgerRecordsWhatWasAppliedAndWhen()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();
        await schema.ApplyAsync(Scripts(("001_a", "CREATE TABLE a (id INT)")));

        Assert.Equal("001_a", await schema.ScalarAsync<string>($"SELECT id FROM {Ledger}"));
        Assert.Equal(0, await schema.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {Ledger} WHERE applied_at IS NULL"));
    }

    [SkippableFact]
    public async Task TwoMigrationSetsInOneDatabase_KeepSeparateLedgersAndDoNotSeeEachOther()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        await schema.ApplyAsync(Scripts(("001_a", "CREATE TABLE set_one (id INT)")), "ledger_one");
        var applied = await schema.ApplyAsync(
            Scripts(("001_a", "CREATE TABLE set_two (id INT)")), "ledger_two");

        // Same migration id in both sets. If the ledger name were not part of the identity, the
        // second set would find '001_a' already applied and silently skip its own table — which is
        // the collision a bare 'schema_migrations' invites in a database shared with another product.
        Assert.Equal(1, applied);
        Assert.Equal(1, await schema.CountTablesAsync("set_one"));
        Assert.Equal(1, await schema.CountTablesAsync("set_two"));
    }

    private static MigrationScript[] Scripts(params (string Id, string Sql)[] scripts) =>
        scripts.Select(s => new MigrationScript(s.Id, s.Sql)).ToArray();
}
