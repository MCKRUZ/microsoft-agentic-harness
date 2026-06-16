using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration;

public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=observability;Username=observability;Password=observability";

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string RunTag { get; } = $"test-{Guid.NewGuid():N}";
    public bool IsAvailable { get; private set; }
    public ILogger<Infrastructure.Observability.Persistence.PostgresObservabilityStore> StoreLogger { get; }
        = NullLogger<Infrastructure.Observability.Persistence.PostgresObservabilityStore>.Instance;

    /// <summary>
    /// True when the connection string was supplied explicitly via the
    /// <c>OBSERVABILITY_TEST_CONN</c> environment variable rather than falling back to the
    /// localhost default. When set, the operator (or CI) is asserting that Postgres is provisioned,
    /// so any connectivity failure is a real defect that must surface loudly rather than silently
    /// disabling the suite.
    /// </summary>
    private static bool IsConnectionExplicitlyConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OBSERVABILITY_TEST_CONN"));

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("OBSERVABILITY_TEST_CONN") ?? DefaultConnectionString;

    /// <summary>
    /// Probes the target Postgres server and sets <see cref="IsAvailable"/>.
    /// <para>
    /// A server that is simply not listening on the default localhost endpoint (connection refused
    /// — i.e. a developer machine with no Postgres running) sets <see cref="IsAvailable"/> to
    /// <c>false</c> so the integration tests can opt out. Every other failure — a reachable server
    /// that rejects the probe (wrong password, missing database, schema drift) or ANY failure when
    /// the connection was configured explicitly via <c>OBSERVABILITY_TEST_CONN</c> — is rethrown so
    /// the fixture fails loudly. This prevents the ~100 integration tests in this collection from
    /// reporting green when Postgres is misconfigured or unreachable in an environment that expected
    /// it to be present (e.g. CI).
    /// </para>
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            DataSource = NpgsqlDataSource.Create(ConnectionString);
            await using var cmd = DataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync();
            IsAvailable = true;
        }
        catch (Exception ex) when (!IsConnectionExplicitlyConfigured && IsServerAbsent(ex))
        {
            // No Postgres listening on the default localhost endpoint and none was demanded via
            // OBSERVABILITY_TEST_CONN — treat as "not provisioned" so local dev runs can skip.
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> only when the failure indicates no server is listening at all
    /// (connection refused / host unreachable). A reachable server that rejects the probe for any
    /// other reason — authentication, missing database, schema problems — is NOT "absent" and must
    /// not be masked as an unavailable fixture.
    /// </summary>
    private static bool IsServerAbsent(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket &&
                (socket.SocketErrorCode is SocketError.ConnectionRefused
                    or SocketError.HostNotFound
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.TimedOut))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Skips the calling test (reported as <em>skipped</em>, not passed) when Postgres is not
    /// available. Tests in this collection must call this instead of an early <c>return</c>: a bare
    /// <c>if (!IsAvailable) return;</c> makes xUnit report the test as a green PASS with zero
    /// assertions executed, so an entire integration suite silently goes green when Postgres is
    /// unreachable and any regression in the persistence layer becomes invisible. Routing the guard
    /// through <c>Skip.IfNot</c> (Xunit.SkippableFact) instead surfaces the opt-out honestly as
    /// a skipped test, keeping the green count meaningful. Callers must be <c>[SkippableFact]</c>.
    /// </summary>
    public void SkipIfUnavailable() =>
        Skip.IfNot(
            IsAvailable,
            "Postgres is not provisioned for this run (set OBSERVABILITY_TEST_CONN or start a local " +
            "Postgres on localhost:5432). The test is skipped rather than reported as a silent pass.");

    public string NewConversationId() => $"{RunTag}-{Guid.NewGuid():N}";

    public async Task<T?> QueryScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? default : (T)Convert.ChangeType(result, typeof(T));
    }

    public async Task<List<Dictionary<string, object?>>> QueryRowsAsync(
        string sql, params NpgsqlParameter[] parameters)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable) return;

        try
        {
            await ExecuteAsync(
                "DELETE FROM audit_log WHERE metadata->>'run_tag' = $1",
                new NpgsqlParameter { Value = RunTag });

            await ExecuteAsync(
                "DELETE FROM sessions WHERE conversation_id LIKE $1",
                new NpgsqlParameter { Value = $"{RunTag}%" });

            // context_snapshots holds conversation_id by value (no FK) so it is
            // not cascade-cleaned by the sessions delete above. Best-effort —
            // table may not exist on older test databases.
            try
            {
                await ExecuteAsync(
                    "DELETE FROM context_snapshots WHERE conversation_id LIKE $1",
                    new NpgsqlParameter { Value = $"{RunTag}%" });
            }
            catch
            {
                // Table doesn't exist on this database — skip.
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        DataSource.Dispose();
    }
}
