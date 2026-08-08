using Infrastructure.Observability.Persistence;
using Infrastructure.Postgres.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tests.Common;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration;

public sealed class PostgresFixture : IAsyncLifetime
{
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string RunTag { get; } = $"test-{Guid.NewGuid():N}";
    public bool IsAvailable { get; private set; }
    public ILogger<Infrastructure.Observability.Persistence.PostgresObservabilityStore> StoreLogger { get; }
        = NullLogger<Infrastructure.Observability.Persistence.PostgresObservabilityStore>.Instance;

    // Connection string, the environment override, what counts as "absent", and the skip wording all
    // come from PostgresAvailability. They used to live here AND in Infrastructure.Postgres.Tests'
    // MigrationTestSchema, which meant the rule for when a Postgres suite may skip was written twice
    // with nothing to catch the two disagreeing.
    public string ConnectionString { get; } = PostgresAvailability.ConnectionString;

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
        catch (Exception ex) when (PostgresAvailability.ShouldSkip(ex))
        {
            // No Postgres listening on the default localhost endpoint and none was demanded via
            // OBSERVABILITY_TEST_CONN — treat as "not provisioned" so local dev runs can skip.
            IsAvailable = false;
        }

        if (IsAvailable) await ApplyMigrationsAsync();
    }

    /// <summary>
    /// Brings the test database up to the schema this assembly ships, using the same runner the
    /// application uses.
    /// </summary>
    /// <remarks>
    /// CI used to do this with a psql loop over <c>Dashboards/init-db/*.sql</c> against a database
    /// created fresh each run. That is why #301 could hide: the one environment able to prove a
    /// schema change worked was also the one environment that never had a database old enough to
    /// need migrating. Going through <see cref="PostgresMigrationRunner"/> means the delivery path
    /// under test is the delivery path that ships — and, on a developer's long-lived local database,
    /// it is genuinely the upgrade path rather than the create path.
    /// </remarks>
    private async Task ApplyMigrationsAsync()
    {
        await using var connection = await DataSource.OpenConnectionAsync();

        var runner = new PostgresMigrationRunner(
            ObservabilityMigrations.Options, ObservabilityMigrations.Load(), NullLogger.Instance);

        await runner.ApplyAsync(connection);
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
    public void SkipIfUnavailable() => Skip.IfNot(IsAvailable, PostgresAvailability.SkipReason);

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
            // not cascade-cleaned by the sessions delete above. The "table may not
            // exist on older test databases" guard that used to wrap this is gone:
            // InitializeAsync now migrates the database, so an older one is brought
            // forward rather than tolerated.
            await ExecuteAsync(
                "DELETE FROM context_snapshots WHERE conversation_id LIKE $1",
                new NpgsqlParameter { Value = $"{RunTag}%" });
        }
        catch
        {
            // Best-effort cleanup
        }

        DataSource.Dispose();
    }
}
