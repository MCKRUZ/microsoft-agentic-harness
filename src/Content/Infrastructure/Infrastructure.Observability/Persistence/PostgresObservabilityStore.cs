using Application.AI.Common.Interfaces;
using Infrastructure.Postgres.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.Observability.Persistence;

/// <summary>
/// Persists and retrieves observability data from PostgreSQL using Npgsql.
/// Designed for append-heavy workloads with fire-and-forget semantics
/// for non-critical writes (audit, safety) to avoid blocking agent turns.
/// Read methods return empty collections on failure to maintain resilience.
/// </summary>
/// <remarks>
/// The schema is owned by this assembly and applied by <see cref="PostgresMigrationRunner"/> on the
/// first physical connection this store opens. It used to arrive from SQL files mounted into the
/// Postgres container, which the server runs only when it creates an empty data directory — so a
/// schema change could never reach a database that already held data.
/// </remarks>
public sealed partial class PostgresObservabilityStore : IObservabilityStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSchemaGate _schema;
    private readonly ILogger<PostgresObservabilityStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresObservabilityStore"/> class.
    /// </summary>
    /// <param name="connectionString">Connection string for the observability database.</param>
    /// <param name="logger">Logger for write failures and schema migration.</param>
    public PostgresObservabilityStore(
        string connectionString,
        ILogger<PostgresObservabilityStore> logger)
    {
        _logger = logger;

        _schema = new PostgresSchemaGate(
            ObservabilityMigrations.Options, ObservabilityMigrations.Load(), logger);

        var builder = new NpgsqlDataSourceBuilder(connectionString);

        // The one place every query in this store passes through. Eighteen call sites take commands
        // straight off the data source, so hooking the physical connection is what lets the schema be
        // guaranteed current without threading an "ensure schema" call through all of them — and it
        // happens before the first query rather than racing it.
        builder.UsePhysicalConnectionInitializer(
            _ => throw new NotSupportedException(
                "The observability store opens connections asynchronously only. A synchronous Open() " +
                "would skip the schema migration this initializer performs."),
            EnsureSchemaAsync);

        _dataSource = builder.Build();
    }

    /// <summary>
    /// Brings the schema up to date on the first physical connection, then does nothing.
    /// </summary>
    /// <remarks>
    /// The failure is logged here, at Error, as well as thrown. Callers on the write path
    /// deliberately swallow exceptions so telemetry can never fail the agent turn that produced it,
    /// which would otherwise reduce a failed schema upgrade to a warning about one query. Logging at
    /// the point of failure keeps the real cause visible whatever the caller does with it.
    /// </remarks>
    private async Task EnsureSchemaAsync(NpgsqlConnection connection)
    {
        try
        {
            await _schema.EnsureAsync(connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Observability schema migration failed. Session, message and tool data cannot be " +
                "persisted until the database is brought up to date.");
            throw;
        }
    }

    private async Task ExecuteNonQuerySafe(
        string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            for (var i = 0; i < parameters.Length; i++)
                cmd.Parameters.AddWithValue(parameters[i]);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Observability store write failed: {Sql}", sql[..Math.Min(sql.Length, 80)]);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _dataSource.Dispose();
}
