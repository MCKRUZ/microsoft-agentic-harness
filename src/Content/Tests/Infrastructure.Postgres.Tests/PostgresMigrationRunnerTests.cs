using Infrastructure.Postgres.Migrations;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// The runner's own guarantees: apply once, apply in order, never half-apply, and stay correct when
/// two hosts start at the same moment.
/// </summary>
public sealed class PostgresMigrationRunnerTests
{
    private const string Ledger = MigrationTestSchema.TestLedgerTable;

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

        // Deliberately NOT idempotent: re-running it raises "relation already exists". That is what
        // makes the second call below evidence rather than a count — if 001 were re-applied the whole
        // run would throw, so a clean result proves it was skipped, not merely tolerated.
        //
        // An earlier version of this test made that point by handing 001 a different body the second
        // time ("SELECT 1/0"). The checksum guard now refuses exactly that, and rightly: same id,
        // different text is the edit-an-applied-migration mistake, so the test was demonstrating its
        // property with the one move the runner exists to forbid.
        const string createA = "CREATE TABLE a (id INT)";

        await schema.ApplyAsync(Scripts(("001_a", createA)));

        var applied = await schema.ApplyAsync(Scripts(
            ("001_a", createA),
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

    /// <summary>
    /// Editing a migration this database already ran stops the run, rather than doing nothing and
    /// letting the installed base split in two.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is silent by construction: the id is in the ledger, so the edited
    /// script is skipped here and applied in full on every database created afterwards. Nothing logs
    /// it, no test goes red, and it is found by hand-diffing a live schema — the same shape as the
    /// defect the whole subsystem exists to end. Note what the control below establishes: re-running
    /// the *unedited* set is a clean no-op, so the throw is caused by the edit and not by the second
    /// run.
    /// </remarks>
    [SkippableFact]
    public async Task EditingAnAlreadyAppliedMigration_StopsTheRun()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var original = Scripts(("001_a", "CREATE TABLE a (id INT)"));
        Assert.Equal(1, await schema.ApplyAsync(original));

        // Control: unedited, the same set re-runs as a no-op.
        Assert.Equal(0, await schema.ApplyAsync(original));

        // Treatment: same id, different body.
        var edited = Scripts(("001_a", "CREATE TABLE a (id INT, added TEXT)"));
        var ex = await Assert.ThrowsAsync<PostgresMigrationException>(() => schema.ApplyAsync(edited));

        Assert.Equal("001_a", ex.MigrationId);
        Assert.Contains("has changed since this database applied it", ex.Message, StringComparison.Ordinal);

        // The edit did not sneak in under the throw.
        Assert.Equal(0, await schema.CountColumnsAsync("a", "added"));
    }

    /// <summary>
    /// Only the line endings differing is not an edit.
    /// </summary>
    /// <remarks>
    /// Migrations ship as embedded resources, so their bytes are whatever git checked out: CRLF on a
    /// Windows developer's machine, LF in Linux CI. A checksum over raw bytes would call every
    /// database tampered the moment it met the other platform — a false alarm loud enough that the
    /// first fix anyone reaches for is switching the check off, which is worse than not having it.
    /// </remarks>
    [SkippableFact]
    public async Task TheSameScriptCheckedOutWithWindowsLineEndings_IsNotTreatedAsAnEdit()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        const string unix = "CREATE TABLE crlf_probe (\n  id INT\n)";
        var windows = unix.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.NotEqual(unix, windows); // the rewrite is the premise

        Assert.Equal(1, await schema.ApplyAsync(Scripts(("001_a", unix))));
        Assert.Equal(0, await schema.ApplyAsync(Scripts(("001_a", windows))));
    }

    /// <summary>
    /// A database migrated before the ledger recorded checksums keeps working, and gains one.
    /// </summary>
    /// <remarks>
    /// Adopt, do not accuse: there is nothing to compare a legacy row against, so refusing to start
    /// would strand every database that this runner migrated before the column existed. Recording the
    /// current text now is what makes the <em>next</em> edit catchable, which is the point.
    /// </remarks>
    [SkippableFact]
    public async Task ALedgerRowWithNoChecksum_IsAdoptedRatherThanRefused()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var scripts = Scripts(("001_a", "CREATE TABLE a (id INT)"));
        await schema.ApplyAsync(scripts);

        // Rewind this database to the pre-checksum ledger shape.
        await schema.ExecuteAsync($"UPDATE {Ledger} SET checksum = NULL");
        Assert.Equal(1, await schema.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {Ledger} WHERE checksum IS NULL"));

        Assert.Equal(0, await schema.ApplyAsync(scripts));

        // Adopted, so the next edit is caught rather than being a second free pass.
        Assert.Equal(0, await schema.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {Ledger} WHERE checksum IS NULL"));
        await Assert.ThrowsAsync<PostgresMigrationException>(
            () => schema.ApplyAsync(Scripts(("001_a", "CREATE TABLE a (id INT, added TEXT)"))));
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
