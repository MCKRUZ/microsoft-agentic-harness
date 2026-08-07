using System.Net.Sockets;
using Npgsql;
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
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=observability;Username=observability;Password=observability";

    private MigrationTestSchema(NpgsqlDataSource dataSource, string schemaName)
    {
        DataSource = dataSource;
        SchemaName = schemaName;
    }

    /// <summary>Data source whose connections already have <see cref="SchemaName"/> as search_path.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>The throwaway schema this instance owns.</summary>
    public string SchemaName { get; }

    /// <summary>
    /// An advisory lock key unique to this schema.
    /// </summary>
    /// <remarks>
    /// Postgres advisory locks are cluster-wide, not schema-scoped. Sharing one key across the suite
    /// would make every test in every parallel class queue behind every other for no reason, while
    /// still proving nothing extra — the one test that genuinely needs two runners to contend simply
    /// uses the same schema, and therefore the same key.
    /// </remarks>
    public long AdvisoryLockKey => BitConverter.ToInt64(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SchemaName)));

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("OBSERVABILITY_TEST_CONN") ?? DefaultConnectionString;

    private static bool IsConnectionExplicitlyConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OBSERVABILITY_TEST_CONN"));

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
        }
        catch (Exception ex) when (!IsConnectionExplicitlyConfigured && IsServerAbsent(ex))
        {
            probe?.Dispose();
            Skip.If(true,
                "Postgres is not provisioned for this run (set OBSERVABILITY_TEST_CONN or start a " +
                "local Postgres on localhost:5432). The test is skipped rather than reported as a " +
                "silent pass.");
            throw; // unreachable; Skip.If throws.
        }

        var schemaName = $"mig_{Guid.NewGuid():N}";
        await using (var create = probe.CreateCommand($"CREATE SCHEMA \"{schemaName}\""))
            await create.ExecuteNonQueryAsync();

        probe.Dispose();

        // Search Path belongs in the connection string, not in a physical-connection initializer.
        // The initializer was the first thing tried and it silently did not hold: Npgsql resets a
        // connection's session state when it goes back to the pool, and the initializer does not
        // re-run when that physical connection is handed out again — so the first command after each
        // physical open landed in the throwaway schema and every command after it landed in public.
        // The tests still passed their early assertions and then failed on counts that had collected
        // every other test's rows, which is a good deal more confusing than an outright error.
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = schemaName };

        return new MigrationTestSchema(NpgsqlDataSource.Create(builder.ConnectionString), schemaName);
    }

    /// <summary>Opens a connection already scoped to this schema.</summary>
    /// <returns>An open connection.</returns>
    public Task<NpgsqlConnection> OpenAsync() => DataSource.OpenConnectionAsync().AsTask();

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

    /// <summary>Runs a statement against this schema.</summary>
    /// <param name="sql">The statement to run.</param>
    public async Task ExecuteAsync(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
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

    private static bool IsServerAbsent(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionRefused
                    or SocketError.HostNotFound
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.TimedOut)
            {
                return true;
            }
        }

        return false;
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
