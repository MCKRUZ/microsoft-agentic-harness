using Infrastructure.Postgres.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// The gate's own behaviour: run once, then get out of the way — and do not hammer a database that
/// has already said no.
/// </summary>
/// <remarks>
/// These use a real Postgres because the thing being counted is database round trips, and a fake
/// runner would let the test agree with itself. The clock is fake so nothing sleeps.
/// </remarks>
public sealed class PostgresSchemaGateTests
{
    private static MigrationScript[] Working() =>
        [new("001_ok", "CREATE TABLE IF NOT EXISTS gate_probe (id INT)")];

    private static MigrationScript[] Broken() =>
        [new("001_broken", "CREATE TABLE gate_broken (id NOT_A_TYPE)")];

    /// <summary>
    /// Valid SQL that fails only if <c>gate_probe</c> already exists.
    /// </summary>
    /// <remarks>
    /// Plain <c>CREATE TABLE</c>, deliberately not the <c>IF NOT EXISTS</c> form <see cref="Working"/>
    /// uses. The recovery test needs a script that fails for a reason outside itself and then stops
    /// failing, so that one gate instance can do both; with <c>IF NOT EXISTS</c> the pre-created table
    /// is a no-op and nothing ever throws.
    /// </remarks>
    /// <summary>
    /// Sleeps server-side long enough to be cancelled mid-flight, then creates the probe table.
    /// </summary>
    /// <remarks>
    /// The sleep is what gives a test a window in which the migration is genuinely running, so a
    /// cancellation arrives from Postgres rather than being rejected by the gate before it starts.
    /// The table creation after it is the evidence a later, uncancelled run actually completed.
    /// </remarks>
    private static MigrationScript[] SlowThenCreatesProbe() =>
        [new("001_slow", "SELECT pg_sleep(1); CREATE TABLE IF NOT EXISTS gate_probe (id INT)")];

    private static MigrationScript[] FailsIfProbeExists() =>
        [new("001_probe", "CREATE TABLE gate_probe (id INT)")];

    [SkippableFact]
    public async Task TheMigrationsRunOnce_HoweverManyConnectionsAsk()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var options = schema.OptionsFor("once");
        var gate = new PostgresSchemaGate(options, Working(), NullLogger.Instance);

        for (var i = 0; i < 5; i++)
        {
            await using var connection = await schema.OpenAsync();
            await gate.EnsureAsync(connection);
        }

        // One ledger row, not five: the second caller onwards short-circuits on the ready flag rather
        // than re-running and relying on the runner to find nothing pending.
        Assert.Equal(1, await schema.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {options.LedgerTable}"));
    }

    /// <summary>
    /// A failure is reported to every caller, but only re-attempted against the database once per
    /// cooldown window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exception identity is the instrument. A run that reaches Postgres builds a new
    /// <see cref="PostgresMigrationException"/> around whatever the server said, so the very same
    /// object coming back twice can only mean the second call never went.
    /// </para>
    /// <para>
    /// The first instrument tried here was the ledger table — present means an attempt happened.
    /// It does not: the whole run is one transaction, so a failure rolls back the
    /// <c>CREATE TABLE IF NOT EXISTS</c> for the ledger along with everything else, which
    /// <c>AFailingMigrationThrows_AndLeavesTheDatabaseAtItsPreviousVersion</c> already asserts. The
    /// test failed for a reason that had nothing to do with the cooldown.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task WithinTheCooldown_AFailureIsReplayedWithoutTouchingTheDatabase()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var clock = new FakeTimeProvider();
        var gate = new PostgresSchemaGate(schema.OptionsFor("cooldown"), Broken(), NullLogger.Instance, clock);

        await using var connection = await schema.OpenAsync();

        var first = await Assert.ThrowsAsync<PostgresMigrationException>(
            () => gate.EnsureAsync(connection));

        clock.Advance(TimeSpan.FromSeconds(4));

        var second = await Assert.ThrowsAsync<PostgresMigrationException>(
            () => gate.EnsureAsync(connection));

        Assert.Same(first, second);
    }

    /// <summary>
    /// After the cooldown the database is asked again, so a fault that has since been fixed recovers
    /// without a restart.
    /// </summary>
    /// <remarks>
    /// This is the property the cooldown must not break, and it is the reason a failure does not
    /// simply latch: Postgres being briefly unreachable while a host starts is ordinary.
    /// </remarks>
    [SkippableFact]
    public async Task AfterTheCooldown_TheDatabaseIsAskedAgain()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var clock = new FakeTimeProvider();
        var gate = new PostgresSchemaGate(schema.OptionsFor("recovery"), Broken(), NullLogger.Instance, clock);

        await using var connection = await schema.OpenAsync();

        var first = await Assert.ThrowsAsync<PostgresMigrationException>(
            () => gate.EnsureAsync(connection));

        clock.Advance(TimeSpan.FromSeconds(6));

        var second = await Assert.ThrowsAsync<PostgresMigrationException>(
            () => gate.EnsureAsync(connection));

        // A different object, so this failure was produced by a fresh run rather than replayed from
        // the cache. Same reasoning as the cooldown test, read the other way round.
        Assert.NotSame(first, second);
    }

    /// <summary>
    /// One gate instance that fails and is then given a working database recovers, and stays ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure is environmental rather than in the script, so the SAME gate can fail and then
    /// succeed: <c>gate_probe</c> is pre-created with a conflicting shape, the script's plain
    /// <c>CREATE TABLE</c> collides with it, and dropping the table repairs the condition without
    /// touching the gate. An earlier version of this test built a second gate for the successful run,
    /// which meant the failing instance's recovery was never exercised at all.
    /// </para>
    /// <para>
    /// <strong>What this deliberately does not claim.</strong> It was originally named for clearing
    /// the cached failure, and review showed it did not test that — deleting the line that clears it
    /// left every test here green. Nothing can test it through behaviour: once the run succeeds the
    /// ready flag short-circuits before the cooldown is ever consulted, so a stale cached failure is
    /// unreachable by construction. That line releases a reference; it is not observable, and the
    /// test no longer pretends otherwise.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task AGateThatFailedAndThenSucceeds_IsReadyAndStaysReady()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var clock = new FakeTimeProvider();
        var gate = new PostgresSchemaGate(
            schema.OptionsFor("recover"), FailsIfProbeExists(), NullLogger.Instance, clock);
        await using var connection = await schema.OpenAsync();

        // Make the working script fail for a reason outside itself.
        await schema.ExecuteAsync("CREATE TABLE gate_probe (other_column TEXT)");
        await Assert.ThrowsAsync<PostgresMigrationException>(() => gate.EnsureAsync(connection));

        await schema.ExecuteAsync("DROP TABLE gate_probe");
        clock.Advance(TimeSpan.FromSeconds(6));

        await gate.EnsureAsync(connection);
        await gate.EnsureAsync(connection);

        Assert.Equal(1, await schema.CountTablesAsync("gate_probe"));
        Assert.Equal(1, await schema.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {schema.OptionsFor("recover").LedgerTable}"));
    }

    /// <summary>
    /// A caller cancelling does not poison the gate for everyone else.
    /// </summary>
    /// <remarks>
    /// The cooldown exists for failures that belong to the database. A cancellation belongs to one
    /// caller, and caching it would replay one client's disconnect to every other caller for the
    /// whole window — including callers whose connection is fine and whose schema would have applied
    /// cleanly. In the observability store, whose writes are swallowed by design, that is a window of
    /// silently dropped telemetry where previously the next caller simply retried and succeeded.
    /// </remarks>
    [SkippableFact]
    public async Task ACancelledCaller_DoesNotPoisonTheCooldownForOthers()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var clock = new FakeTimeProvider();
        var gate = new PostgresSchemaGate(
            schema.OptionsFor("cancel"), SlowThenCreatesProbe(), NullLogger.Instance, clock);

        // Cancelled DURING the migration, not before it. An already-cancelled token would throw at
        // the in-process lock on the first line of EnsureAsync, before the runner is reached — which
        // is how the first version of this test managed to pass with the guard deleted. The failure
        // has to come back from Postgres for the caching decision to be exercised at all.
        await using (var slow = await schema.OpenAsync())
        using (var cancelling = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => gate.EnsureAsync(slow, cancelling.Token));
        }

        // No clock advance: still well inside the cooldown window. This caller has its own healthy
        // connection and a live token, so it must be served by the database rather than by a cached
        // copy of somebody else's cancellation.
        await using var healthy = await schema.OpenAsync();
        await gate.EnsureAsync(healthy);

        Assert.Equal(1, await schema.CountTablesAsync("gate_probe"));
    }
}
