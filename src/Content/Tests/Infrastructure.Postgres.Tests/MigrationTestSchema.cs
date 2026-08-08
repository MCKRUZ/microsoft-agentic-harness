using System.Security.Cryptography;
using System.Text;
using Infrastructure.Postgres.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tests.Common;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// Gives each test its own empty Postgres schema, so a migration set can be applied from nothing and
/// inspected without any test seeing another's tables.
/// </summary>
/// <remarks>
/// <para>
/// A schema rather than a database. Creating a database needs a connection to a different one and
/// costs a template copy per test; a schema plus <c>search_path</c> is nearly free, and every
/// migration in this repo names its objects unqualified, so they land in the right place with no
/// changes. The cost is that <c>current_schema()</c> matters — migration 005 filters
/// <c>pg_constraint</c> by it, which is correct here and correct in production for the same reason.
/// </para>
/// <para>
/// Connectivity is treated exactly as <c>PostgresFixture</c> treats it: a server that is simply not
/// listening means "not provisioned" and the tests skip; anything else is a real defect and is
/// rethrown. Skipping is reported as a skip, never as a pass — a suite that goes green with zero
/// assertions run is worse than one that goes red.
/// </para>
/// </remarks>
public sealed class MigrationTestSchema : IAsyncDisposable
{
    /// <summary>
    /// Ledger table used by tests that do not care which ledger they write to.
    /// </summary>
    /// <remarks>
    /// Public because <c>PostgresMigrationRunnerTests</c> queries this table directly to count applied
    /// rows. With the name written out in both places, changing it here left those assertions reading
    /// a table the runner never wrote to — <c>COUNT(*)</c> of nothing, still looking meaningful.
    /// </remarks>
    public const string TestLedgerTable = "test_schema_migrations";

    private MigrationTestSchema(NpgsqlDataSource dataSource, string schemaName)
    {
        DataSource = dataSource;
        SchemaName = schemaName;

        // Computed once. As an expression-bodied property this re-hashed on every read, which reads
        // as a constant and was not one.
        AdvisoryLockKey = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(schemaName)));
    }

    /// <summary>Data source whose connections resolve unqualified names to <see cref="SchemaName"/>.</summary>
    private NpgsqlDataSource DataSource { get; }

    /// <summary>The throwaway schema this instance owns.</summary>
    private string SchemaName { get; }

    /// <summary>
    /// An advisory lock key unique to this schema.
    /// </summary>
    /// <remarks>
    /// Postgres advisory locks are cluster-wide, not schema-scoped. Sharing one key across the suite
    /// would make every test in every parallel class queue behind every other for no reason, while
    /// still proving nothing extra — the one test that genuinely needs two runners to contend simply
    /// uses the same schema, and therefore the same key.
    /// </remarks>
    private long AdvisoryLockKey { get; }

    // Connection string, the environment override, what counts as an absent server, and the skip
    // wording all come from PostgresAvailability, which Infrastructure.Observability.Tests' fixture
    // also uses. Both suites had their own copy; that made the rule for when a Postgres suite may
    // skip a thing stated twice, with nothing to notice the two drifting apart.
    private static string ConnectionString => PostgresAvailability.ConnectionString;

    /// <summary>
    /// Creates an empty schema and returns a data source scoped to it, or skips the calling test when
    /// no Postgres is provisioned. Callers must be <c>[SkippableFact]</c>.
    /// </summary>
    /// <returns>The schema handle; dispose it to drop the schema.</returns>
    public static async Task<MigrationTestSchema> CreateAsync()
    {
        NpgsqlDataSource? probe = null;
        try
        {
            probe = NpgsqlDataSource.Create(ConnectionString);

            await using (var ping = probe.CreateCommand("SELECT 1"))
                await ping.ExecuteScalarAsync();

            var schemaName = $"mig_{Guid.NewGuid():N}";
            await using (var create = probe.CreateCommand($"CREATE SCHEMA \"{schemaName}\""))
                await create.ExecuteNonQueryAsync();

            // Search Path belongs in the connection string, not in a physical-connection initializer.
            // The initializer was the first thing tried and it silently did not hold: Npgsql resets a
            // connection's session state when it goes back to the pool, and the initializer does not
            // re-run when that physical connection is handed out again — so the first command after
            // each physical open landed in the throwaway schema and every command after it landed in
            // public. The tests still passed their early assertions and then failed on counts that had
            // collected every other test's rows, which is a good deal more confusing than an error.
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = schemaName };

            return new MigrationTestSchema(NpgsqlDataSource.Create(builder.ConnectionString), schemaName);
        }
        catch (Exception ex) when (PostgresAvailability.ShouldSkip(ex))
        {
            Skip.If(true, PostgresAvailability.SkipReason);
            throw; // unreachable; Skip.If throws.
        }
        finally
        {
            // In a finally, not on each exit path. It was written per-path first and leaked the probe
            // whenever CREATE SCHEMA itself failed — a data source and its whole pool, per failure,
            // for the life of the test process.
            probe?.Dispose();
        }
    }

    /// <summary>
    /// Applies a migration set to this schema and returns how many ran.
    /// </summary>
    /// <param name="scripts">The migrations to apply.</param>
    /// <param name="ledgerTable">Ledger table to record them in; defaults to a test-owned name.</param>
    /// <returns>The number of migrations applied.</returns>
    public async Task<int> ApplyAsync(
        IReadOnlyList<MigrationScript> scripts, string ledgerTable = TestLedgerTable)
    {
        var runner = new PostgresMigrationRunner(
            new PostgresMigrationOptions(ledgerTable, AdvisoryLockKey),
            scripts,
            NullLogger.Instance);

        await using var connection = await OpenAsync();
        return await runner.ApplyAsync(connection);
    }

    /// <summary>Opens a connection already scoped to this schema.</summary>
    /// <returns>An open connection.</returns>
    public Task<NpgsqlConnection> OpenAsync() => DataSource.OpenConnectionAsync().AsTask();

    /// <summary>Counts tables with the given name in this schema — 0 or 1.</summary>
    /// <param name="tableName">Unqualified table name.</param>
    /// <returns>1 if the table exists, otherwise 0.</returns>
    public Task<int> CountTablesAsync(string tableName) =>
        ScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.tables " +
            $"WHERE table_schema = '{SchemaName}' AND table_name = '{tableName}'");

    /// <summary>Counts columns with the given name on the given table in this schema — 0 or 1.</summary>
    /// <param name="tableName">Unqualified table name.</param>
    /// <param name="columnName">Column name.</param>
    /// <returns>1 if the column exists, otherwise 0.</returns>
    public Task<int> CountColumnsAsync(string tableName, string columnName) =>
        ScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.columns " +
            $"WHERE table_schema = '{SchemaName}' AND table_name = '{tableName}' " +
            $"AND column_name = '{columnName}'");

    /// <summary>Runs a statement against this schema and returns the first column of the first row.</summary>
    /// <typeparam name="T">Expected scalar type.</typeparam>
    /// <param name="sql">The statement to run.</param>
    /// <returns>The scalar value, or <c>default</c> when the result is null or empty.</returns>
    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? default : (T)Convert.ChangeType(result, typeof(T));
    }

    /// <summary>Runs a statement and returns the exception it raised, or null if it succeeded.</summary>
    /// <param name="sql">The statement to run.</param>
    /// <returns>The failure, or <c>null</c>.</returns>
    public async Task<Exception?> TryExecuteAsync(string sql)
    {
        try
        {
            await ExecuteAsync(sql);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Runs a statement against this schema.</summary>
    /// <param name="sql">The statement to run.</param>
    public async Task ExecuteAsync(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var drop = DataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{SchemaName}\" CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort cleanup; a leaked test schema is noise, not a failure.
        }

        await DataSource.DisposeAsync();
    }
}
