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
    private static readonly PostgresMigrationOptions Options =
        new("gate_test_migrations", 0x67617465745F31L);

    private static MigrationScript[] Working() =>
        [new("001_ok", "CREATE TABLE IF NOT EXISTS gate_probe (id INT)")];

    private static MigrationScript[] Broken() =>
        [new("001_broken", "CREATE TABLE gate_broken (id NOT_A_TYPE)")];

    [SkippableFact]
    public async Task TheMigrationsRunOnce_HoweverManyConnectionsAsk()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var gate = new PostgresSchemaGate(Options, Working(), NullLogger.Instance);

        for (var i = 0; i < 5; i++)
        {
            await using var connection = await schema.OpenAsync();
            await gate.EnsureAsync(connection);
        }

        // One ledger row, not five: the second caller onwards short-circuits on the ready flag rather
        // than re-running and relying on the runner to find nothing pending.
        Assert.Equal(1, await schema.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {Options.LedgerTable}"));
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
        var gate = new PostgresSchemaGate(Options, Broken(), NullLogger.Instance, clock);

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
        var gate = new PostgresSchemaGate(Options, Broken(), NullLogger.Instance, clock);

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
    /// A gate that failed and then succeeds forgets the failure.
    /// </summary>
    /// <remarks>
    /// Without clearing it, a gate that recovered would still be holding a stale exception. Nothing
    /// would read it while <c>_ready</c> is set, so this is not a live defect — it is the assertion
    /// that keeps it from becoming one if the ready check is ever reordered.
    /// </remarks>
    [SkippableFact]
    public async Task ASuccessfulRunAfterAFailure_ClearsTheCachedFailure()
    {
        await using var schema = await MigrationTestSchema.CreateAsync();

        var clock = new FakeTimeProvider();
        var broken = new PostgresSchemaGate(Options, Broken(), NullLogger.Instance, clock);
        await using var connection = await schema.OpenAsync();

        await Assert.ThrowsAsync<PostgresMigrationException>(() => broken.EnsureAsync(connection));

        var fixedGate = new PostgresSchemaGate(Options, Working(), NullLogger.Instance, clock);
        await fixedGate.EnsureAsync(connection);
        await fixedGate.EnsureAsync(connection);

        Assert.Equal(1, await schema.CountTablesAsync("gate_probe"));
    }
}
