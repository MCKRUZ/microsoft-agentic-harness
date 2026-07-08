using System.Text.Json;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.GraphRag;

/// <summary>
/// SQLite-backed implementation of <see cref="IGraphDatabaseBackend"/> using an embedded
/// relational schema to model graph structure. Named "KuzuGraphBackend" to align with the
/// project's planned Kuzu integration — once stable .NET bindings ship this class can be
/// swapped without changing the interface contract.
/// </summary>
/// <remarks>
/// <para>
/// All four tables (Nodes, Edges, CommunityAssignments, Communities) are created on first
/// open. The database file lives at <c>{dataDirectory}/graph.db</c>.
/// </para>
/// <para>
/// <strong>SQL injection safety:</strong> all user-supplied values are passed exclusively
/// through <see cref="SqliteParameter"/> instances. Multi-value <c>IN</c> clauses are
/// implemented via a SQLite session-scoped temp table (<c>_TempIds</c>) that is populated
/// with individual parameterized inserts — no string interpolation of user data occurs at
/// any point.
/// </para>
/// <para>
/// Thread safety: a single <see cref="SqliteConnection"/> is held open for the lifetime of
/// the instance. Callers must not share one instance across concurrent execution contexts
/// without external synchronization. For concurrent access, create separate instances
/// pointing to the same file (SQLite WAL mode is enabled).
/// </para>
/// </remarks>
public sealed partial class KuzuGraphBackend : IGraphDatabaseBackend, IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly ILogger<KuzuGraphBackend> _logger;

    /// <summary>
    /// Opens (or creates) the graph database in <paramref name="dataDirectory"/> and
    /// initializes the schema.
    /// </summary>
    /// <param name="dataDirectory">Directory where <c>graph.db</c> will be stored.</param>
    /// <param name="logger">Logger for recording graph operations.</param>
    public KuzuGraphBackend(string dataDirectory, ILogger<KuzuGraphBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        Directory.CreateDirectory(dataDirectory);

        var dbPath = Path.Combine(dataDirectory, "graph.db");
        _connection = new SqliteConnection($"Data Source={dbPath};");
        _connection.Open();

        EnableWalMode();
        InitializeSchema();

        _logger.LogDebug("KuzuGraphBackend opened database at {DbPath}", dbPath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Checkpoint WAL and revert to DELETE journal mode so the -wal and -shm sidecar
        // files are removed before the connection closes. Without this, Windows file locks
        // on those sidecars prevent callers from deleting the data directory immediately
        // after Dispose (relevant to test teardown and container restart scenarios).
        try
        {
            using var checkpoint = _connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
            checkpoint.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WAL checkpoint on dispose failed — sidecar files may linger");
        }

        _connection.Dispose();

        // On Windows, the native SQLite file handles are pooled and not released until the
        // connection pool is cleared. Without this call, Directory.Delete() immediately after
        // Dispose() fails with IOException (file in use). ClearAllPools() flushes the pool
        // for this connection string, releasing the OS-level file locks synchronously.
        SqliteConnection.ClearAllPools();

        _logger.LogDebug("KuzuGraphBackend disposed");
    }

    private void EnableWalMode()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Nodes (
                id               TEXT PRIMARY KEY,
                name             TEXT NOT NULL,
                type             TEXT NOT NULL,
                chunk_ids_json   TEXT NOT NULL DEFAULT '[]',
                properties_json  TEXT NOT NULL DEFAULT '{}',
                weight           REAL NOT NULL DEFAULT 1.0,
                owner_id         TEXT,
                created_at       TEXT,
                expires_at       TEXT
            );

            CREATE TABLE IF NOT EXISTS Edges (
                id               TEXT PRIMARY KEY,
                source_node_id   TEXT NOT NULL,
                target_node_id   TEXT NOT NULL,
                predicate        TEXT NOT NULL,
                chunk_id         TEXT NOT NULL,
                properties_json  TEXT NOT NULL DEFAULT '{}',
                owner_id         TEXT,
                created_at       TEXT,
                expires_at       TEXT
            );

            CREATE TABLE IF NOT EXISTS CommunityAssignments (
                node_id       TEXT NOT NULL,
                community_id  TEXT NOT NULL,
                level         INTEGER NOT NULL,
                PRIMARY KEY (node_id, level)
            );

            CREATE TABLE IF NOT EXISTS Communities (
                id            TEXT PRIMARY KEY,
                level         INTEGER NOT NULL,
                summary       TEXT NOT NULL,
                node_ids_json TEXT NOT NULL DEFAULT '[]',
                modularity    REAL NOT NULL DEFAULT 0.0
            );

            CREATE INDEX IF NOT EXISTS idx_edges_source ON Edges (source_node_id);
            CREATE INDEX IF NOT EXISTS idx_edges_target ON Edges (target_node_id);
            CREATE INDEX IF NOT EXISTS idx_comm_level   ON Communities (level);
            CREATE INDEX IF NOT EXISTS idx_assign_comm  ON CommunityAssignments (community_id);
            CREATE INDEX IF NOT EXISTS idx_nodes_owner  ON Nodes (owner_id);
            """;

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Replaces the contents of the session-scoped temp table <c>_TempIds</c> with the
    /// provided IDs. All values are inserted via parameterized statements — no user data
    /// appears in any SQL string. Callers then JOIN against <c>_TempIds</c> using a static
    /// SQL literal, eliminating all dynamic <c>IN (…)</c> string construction.
    /// </summary>
    private async Task PopulateTempTableAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        // Create (idempotent) and clear.
        using (var createCmd = _connection.CreateCommand())
        {
            createCmd.Transaction = transaction;
            createCmd.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS _TempIds (id TEXT PRIMARY KEY);
                DELETE FROM _TempIds;
                """;
            await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Insert each ID individually via a prepared statement — safe by construction.
        using var insertCmd = _connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = "INSERT OR IGNORE INTO _TempIds (id) VALUES (@id)";
        var param = insertCmd.Parameters.Add("@id", SqliteType.Text);

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            param.Value = id;
            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertNodeAsync(GraphNode node)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Nodes
                (id, name, type, chunk_ids_json, properties_json, weight, owner_id, created_at, expires_at)
            VALUES
                (@id, @name, @type, @chunks, @props, 1.0, @owner, @createdAt, @expiresAt)
            """;

        cmd.Parameters.AddWithValue("@id", node.Id);
        cmd.Parameters.AddWithValue("@name", node.Name);
        cmd.Parameters.AddWithValue("@type", node.Type);
        cmd.Parameters.AddWithValue("@chunks",
            JsonSerializer.Serialize(node.ChunkIds, _jsonOptions));
        cmd.Parameters.AddWithValue("@props", SerializeDict(node.Properties));
        cmd.Parameters.AddWithValue("@owner", node.OwnerId as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", FormatDate(node.CreatedAt));
        cmd.Parameters.AddWithValue("@expiresAt", FormatDate(node.ExpiresAt));

        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task UpdateNodeChunkIdsAndPropertiesAsync(
        string nodeId,
        IReadOnlyList<string> chunkIds,
        IReadOnlyDictionary<string, string> properties)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Nodes
            SET chunk_ids_json  = @chunks,
                properties_json = @props
            WHERE id = @id
            """;

        cmd.Parameters.AddWithValue("@chunks",
            JsonSerializer.Serialize(chunkIds, _jsonOptions));
        cmd.Parameters.AddWithValue("@props", SerializeDict(properties));
        cmd.Parameters.AddWithValue("@id", nodeId);

        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<List<GraphNode>> ReadNodesAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var nodes = new List<GraphNode>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            nodes.Add(ReadNodeFromReader(reader));
        return nodes;
    }

    private static GraphNode ReadNodeFromReader(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Type = reader.GetString(reader.GetOrdinal("type")),
            ChunkIds = JsonSerializer.Deserialize<List<string>>(
                reader.GetString(reader.GetOrdinal("chunk_ids_json")), _jsonOptions) ?? [],
            Properties = DeserializeDict(reader.GetString(reader.GetOrdinal("properties_json"))),
            OwnerId = reader.IsDBNull(reader.GetOrdinal("owner_id"))
                ? null : reader.GetString(reader.GetOrdinal("owner_id")),
            CreatedAt = ParseDate(reader.IsDBNull(reader.GetOrdinal("created_at"))
                ? null : reader.GetString(reader.GetOrdinal("created_at"))),
            ExpiresAt = ParseDate(reader.IsDBNull(reader.GetOrdinal("expires_at"))
                ? null : reader.GetString(reader.GetOrdinal("expires_at")))
        };

    private static GraphNode ReadNodeFromColumns(
        SqliteDataReader reader,
        string idCol, string nameCol, string typeCol,
        string chunksCol, string propsCol,
        string ownerCol, string createdCol, string expiresCol) =>
        new()
        {
            Id = reader.GetString(reader.GetOrdinal(idCol)),
            Name = reader.GetString(reader.GetOrdinal(nameCol)),
            Type = reader.GetString(reader.GetOrdinal(typeCol)),
            ChunkIds = JsonSerializer.Deserialize<List<string>>(
                reader.GetString(reader.GetOrdinal(chunksCol)), _jsonOptions) ?? [],
            Properties = DeserializeDict(reader.GetString(reader.GetOrdinal(propsCol))),
            OwnerId = reader.IsDBNull(reader.GetOrdinal(ownerCol))
                ? null : reader.GetString(reader.GetOrdinal(ownerCol)),
            CreatedAt = ParseDate(reader.IsDBNull(reader.GetOrdinal(createdCol))
                ? null : reader.GetString(reader.GetOrdinal(createdCol))),
            ExpiresAt = ParseDate(reader.IsDBNull(reader.GetOrdinal(expiresCol))
                ? null : reader.GetString(reader.GetOrdinal(expiresCol)))
        };

    private static string SerializeDict(IReadOnlyDictionary<string, string> dict) =>
        JsonSerializer.Serialize(dict, _jsonOptions);

    private static IReadOnlyDictionary<string, string> DeserializeDict(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions)
            ?? new Dictionary<string, string>();

    private static object FormatDate(DateTimeOffset? date) =>
        date.HasValue ? (object)date.Value.ToString("O") : DBNull.Value;

    private static DateTimeOffset? ParseDate(string? value) =>
        value is null
            ? null
            : DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static IReadOnlyDictionary<string, string> MergeProperties(
        IReadOnlyDictionary<string, string> existing,
        IReadOnlyDictionary<string, string> incoming)
    {
        var merged = new Dictionary<string, string>(existing);
        foreach (var kvp in incoming)
            merged[kvp.Key] = kvp.Value;
        return merged;
    }
}
