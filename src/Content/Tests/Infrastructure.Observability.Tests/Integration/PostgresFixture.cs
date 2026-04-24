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

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("OBSERVABILITY_TEST_CONN") ?? DefaultConnectionString;

    public async Task InitializeAsync()
    {
        try
        {
            DataSource = NpgsqlDataSource.Create(ConnectionString);
            await using var cmd = DataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync();
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

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
        }
        catch
        {
            // Best-effort cleanup
        }

        DataSource.Dispose();
    }
}
