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
public sealed class KuzuGraphBackend : IGraphDatabaseBackend, IDisposable
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

    // ── IKnowledgeGraphStore ─────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task AddNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default)
    {
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check existence first so we can merge ChunkIds on duplicate.
            var existing = await GetNodeAsync(node.Id, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                await InsertNodeAsync(node).ConfigureAwait(false);
            }
            else
            {
                var mergedChunkIds = existing.ChunkIds
                    .Concat(node.ChunkIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var mergedProperties = MergeProperties(existing.Properties, node.Properties);
                await UpdateNodeChunkIdsAndPropertiesAsync(
                    node.Id, mergedChunkIds, mergedProperties).ConfigureAwait(false);
            }
        }

        _logger.LogDebug("AddNodesAsync: upserted {Count} nodes", nodes.Count);
    }

    /// <inheritdoc />
    public async Task AddEdgesAsync(
        IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default)
    {
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO Edges
                    (id, source_node_id, target_node_id, predicate, chunk_id,
                     properties_json, owner_id, created_at, expires_at)
                VALUES
                    (@id, @src, @tgt, @pred, @chunkId,
                     @props, @owner, @createdAt, @expiresAt)
                """;

            cmd.Parameters.AddWithValue("@id", edge.Id);
            cmd.Parameters.AddWithValue("@src", edge.SourceNodeId);
            cmd.Parameters.AddWithValue("@tgt", edge.TargetNodeId);
            cmd.Parameters.AddWithValue("@pred", edge.Predicate);
            cmd.Parameters.AddWithValue("@chunkId", edge.ChunkId);
            cmd.Parameters.AddWithValue("@props", SerializeDict(edge.Properties));
            cmd.Parameters.AddWithValue("@owner", edge.OwnerId as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", FormatDate(edge.CreatedAt));
            cmd.Parameters.AddWithValue("@expiresAt", FormatDate(edge.ExpiresAt));

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("AddEdgesAsync: inserted {Count} edges (duplicates ignored)", edges.Count);
    }

    /// <inheritdoc />
    public async Task<GraphNode?> GetNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Nodes WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", nodeId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadNodeFromReader(reader)
            : null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        string nodeId,
        int maxDepth = 1,
        CancellationToken cancellationToken = default) =>
        TraverseAsync(nodeId, maxDepth, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphTriplet>> GetTripletsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        const string allTripletsSql = """
            SELECT
                e.id AS edge_id, e.source_node_id, e.target_node_id, e.predicate, e.chunk_id,
                e.properties_json AS edge_props, e.owner_id AS edge_owner,
                e.created_at AS edge_created, e.expires_at AS edge_expires,
                s.id AS s_id, s.name AS s_name, s.type AS s_type,
                s.chunk_ids_json AS s_chunks, s.properties_json AS s_props,
                s.owner_id AS s_owner, s.created_at AS s_created, s.expires_at AS s_expires,
                t.id AS t_id, t.name AS t_name, t.type AS t_type,
                t.chunk_ids_json AS t_chunks, t.properties_json AS t_props,
                t.owner_id AS t_owner, t.created_at AS t_created, t.expires_at AS t_expires
            FROM Edges e
            JOIN Nodes s ON s.id = e.source_node_id
            JOIN Nodes t ON t.id = e.target_node_id
            """;

        const string filteredTripletsSql = """
            SELECT
                e.id AS edge_id, e.source_node_id, e.target_node_id, e.predicate, e.chunk_id,
                e.properties_json AS edge_props, e.owner_id AS edge_owner,
                e.created_at AS edge_created, e.expires_at AS edge_expires,
                s.id AS s_id, s.name AS s_name, s.type AS s_type,
                s.chunk_ids_json AS s_chunks, s.properties_json AS s_props,
                s.owner_id AS s_owner, s.created_at AS s_created, s.expires_at AS s_expires,
                t.id AS t_id, t.name AS t_name, t.type AS t_type,
                t.chunk_ids_json AS t_chunks, t.properties_json AS t_props,
                t.owner_id AS t_owner, t.created_at AS t_created, t.expires_at AS t_expires
            FROM Edges e
            JOIN Nodes s ON s.id = e.source_node_id
            JOIN Nodes t ON t.id = e.target_node_id
            WHERE e.source_node_id IN (SELECT id FROM _TempIds)
               OR e.target_node_id  IN (SELECT id FROM _TempIds)
            """;

        using var cmd = _connection.CreateCommand();

        if (nodeIds.Count == 0)
        {
            cmd.CommandText = allTripletsSql;
        }
        else
        {
            await PopulateTempTableAsync(nodeIds, cancellationToken).ConfigureAwait(false);
            cmd.CommandText = filteredTripletsSql;
        }

        var triplets = new List<GraphTriplet>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var source = ReadNodeFromColumns(reader,
                idCol: "s_id", nameCol: "s_name", typeCol: "s_type",
                chunksCol: "s_chunks", propsCol: "s_props",
                ownerCol: "s_owner", createdCol: "s_created", expiresCol: "s_expires");

            var target = ReadNodeFromColumns(reader,
                idCol: "t_id", nameCol: "t_name", typeCol: "t_type",
                chunksCol: "t_chunks", propsCol: "t_props",
                ownerCol: "t_owner", createdCol: "t_created", expiresCol: "t_expires");

            var edge = new GraphEdge
            {
                Id = reader.GetString(reader.GetOrdinal("edge_id")),
                SourceNodeId = reader.GetString(reader.GetOrdinal("source_node_id")),
                TargetNodeId = reader.GetString(reader.GetOrdinal("target_node_id")),
                Predicate = reader.GetString(reader.GetOrdinal("predicate")),
                ChunkId = reader.GetString(reader.GetOrdinal("chunk_id")),
                Properties = DeserializeDict(reader.GetString(reader.GetOrdinal("edge_props"))),
                OwnerId = reader.IsDBNull(reader.GetOrdinal("edge_owner"))
                    ? null : reader.GetString(reader.GetOrdinal("edge_owner")),
                CreatedAt = ParseDate(reader.IsDBNull(reader.GetOrdinal("edge_created"))
                    ? null : reader.GetString(reader.GetOrdinal("edge_created"))),
                ExpiresAt = ParseDate(reader.IsDBNull(reader.GetOrdinal("edge_expires"))
                    ? null : reader.GetString(reader.GetOrdinal("edge_expires")))
            };

            triplets.Add(new GraphTriplet { Source = source, Edge = edge, Target = target });
        }

        return triplets;
    }

    /// <inheritdoc />
    public async Task<bool> NodeExistsAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", nodeId);
        var count = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return count > 0;
    }

    /// <inheritdoc />
    public async Task DeleteNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        // Cascade: remove connected edges, community assignments, then the node itself.
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM Edges WHERE source_node_id = @id OR target_node_id = @id";
            cmd.Parameters.AddWithValue("@id", nodeId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM CommunityAssignments WHERE node_id = @id";
            cmd.Parameters.AddWithValue("@id", nodeId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM Nodes WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", nodeId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Deleted node {NodeId} and its connected edges/assignments", nodeId);
    }

    /// <inheritdoc />
    public async Task DeleteEdgeAsync(
        string edgeId,
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Edges WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", edgeId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetNodeCountAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Nodes";
        return (int)(long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<int> GetEdgeCountAsync(CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Edges";
        return (int)(long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNodesByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Nodes WHERE owner_id = @ownerId";
        cmd.Parameters.AddWithValue("@ownerId", ownerId);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadNodesAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Nodes";
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadNodesAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    // ── IGraphDatabaseBackend ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetCommunityNodesAsync(
        string communityId,
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT n.*
            FROM Nodes n
            JOIN CommunityAssignments ca ON ca.node_id = n.id
            WHERE ca.community_id = @communityId
            """;
        cmd.Parameters.AddWithValue("@communityId", communityId);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadNodesAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Community>> GetCommunitiesAsync(
        int level,
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Communities WHERE level = @level";
        cmd.Parameters.AddWithValue("@level", level);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var communities = new List<Community>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var nodeIds = JsonSerializer.Deserialize<List<string>>(
                reader.GetString(reader.GetOrdinal("node_ids_json")), _jsonOptions) ?? [];

            communities.Add(new Community
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                Level = reader.GetInt32(reader.GetOrdinal("level")),
                Summary = reader.GetString(reader.GetOrdinal("summary")),
                NodeIds = nodeIds,
                Modularity = reader.GetDouble(reader.GetOrdinal("modularity"))
            });
        }

        return communities;
    }

    /// <inheritdoc />
    public async Task AssignCommunityAsync(
        string nodeId,
        string communityId,
        int level,
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO CommunityAssignments (node_id, community_id, level)
            VALUES (@nodeId, @communityId, @level)
            """;
        cmd.Parameters.AddWithValue("@nodeId", nodeId);
        cmd.Parameters.AddWithValue("@communityId", communityId);
        cmd.Parameters.AddWithValue("@level", level);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateNodeWeightAsync(
        string nodeId,
        double weight,
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Nodes SET weight = @weight WHERE id = @id";
        cmd.Parameters.AddWithValue("@weight", weight);
        cmd.Parameters.AddWithValue("@id", nodeId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> TraverseAsync(
        string startNodeId,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        // BFS: start node is excluded from output. The temp table holds the current frontier;
        // neighbors are discovered via static SQL that JOINs _TempIds — no user data is
        // interpolated into SQL strings at any point.
        var visited = new HashSet<string>(StringComparer.Ordinal) { startNodeId };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { startNodeId };

        for (var depth = 0; depth < maxDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frontier.Count == 0) break;

            await PopulateTempTableAsync(frontier, cancellationToken).ConfigureAwait(false);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT
                    CASE WHEN source_node_id IN (SELECT id FROM _TempIds) THEN target_node_id
                         ELSE source_node_id END AS neighbor_id
                FROM Edges
                WHERE source_node_id IN (SELECT id FROM _TempIds)
                   OR target_node_id  IN (SELECT id FROM _TempIds)
                """;

            var nextFrontier = new HashSet<string>(StringComparer.Ordinal);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var neighborId = reader.GetString(0);
                if (visited.Add(neighborId))
                    nextFrontier.Add(neighborId);
            }

            frontier = nextFrontier;
        }

        visited.Remove(startNodeId);
        if (visited.Count == 0)
            return [];

        // Fetch all discovered nodes via the temp table — no interpolation needed.
        await PopulateTempTableAsync(visited, cancellationToken).ConfigureAwait(false);

        using var fetchCmd = _connection.CreateCommand();
        fetchCmd.CommandText = "SELECT * FROM Nodes WHERE id IN (SELECT id FROM _TempIds)";
        using var nodeReader = await fetchCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadNodesAsync(nodeReader, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveCommunityAsync(
        Community community,
        CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO Communities (id, level, summary, node_ids_json, modularity)
            VALUES (@id, @level, @summary, @nodeIds, @modularity)
            """;
        cmd.Parameters.AddWithValue("@id", community.Id);
        cmd.Parameters.AddWithValue("@level", community.Level);
        cmd.Parameters.AddWithValue("@summary", community.Summary);
        cmd.Parameters.AddWithValue("@nodeIds",
            JsonSerializer.Serialize(community.NodeIds, _jsonOptions));
        cmd.Parameters.AddWithValue("@modularity", community.Modularity);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

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

    // ── Private helpers ──────────────────────────────────────────────────────

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
        CancellationToken cancellationToken)
    {
        // Create (idempotent) and clear.
        using (var createCmd = _connection.CreateCommand())
        {
            createCmd.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS _TempIds (id TEXT PRIMARY KEY);
                DELETE FROM _TempIds;
                """;
            await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Insert each ID individually via a prepared statement — safe by construction.
        using var insertCmd = _connection.CreateCommand();
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
