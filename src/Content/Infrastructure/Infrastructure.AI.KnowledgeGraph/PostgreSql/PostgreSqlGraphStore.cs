using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Config;
using Npgsql;

namespace Infrastructure.AI.KnowledgeGraph.PostgreSql;

/// <summary>
/// PostgreSQL implementation of <see cref="IKnowledgeGraphStore"/> using adjacency-list
/// tables with JSONB properties and recursive CTEs for neighbor traversal.
/// Registered with keyed DI key <c>"postgresql"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Schema uses two tables: <c>kg_nodes</c> and <c>kg_edges</c> with foreign key
/// relationships. Properties are stored as JSONB for flexible schema evolution.
/// Neighbor traversal uses <c>WITH RECURSIVE</c> CTEs, which handle multi-hop
/// queries efficiently without requiring a dedicated graph extension like Apache AGE.
/// </para>
/// <para>
/// Connection pooling is managed by Npgsql internally. The connection string is
/// read from <c>AppConfig.AI.Rag.GraphRag.ConnectionString</c>.
/// </para>
/// </remarks>
public sealed class PostgreSqlGraphStore : IKnowledgeGraphStore
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.KnowledgeGraph.PostgreSql");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string SchemaDdl = """
        CREATE TABLE IF NOT EXISTS kg_nodes (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            type        TEXT NOT NULL,
            properties  JSONB,
            chunk_ids   TEXT[],
            provenance  JSONB,
            owner_id    TEXT,
            tenant_id   TEXT,
            created_at  TIMESTAMPTZ,
            expires_at  TIMESTAMPTZ
        );
        CREATE TABLE IF NOT EXISTS kg_edges (
            id              TEXT PRIMARY KEY,
            source_node_id  TEXT NOT NULL,
            target_node_id  TEXT NOT NULL,
            predicate       TEXT NOT NULL,
            properties      JSONB,
            chunk_id        TEXT,
            provenance      JSONB,
            owner_id        TEXT,
            tenant_id       TEXT,
            created_at      TIMESTAMPTZ,
            expires_at      TIMESTAMPTZ
        );
        CREATE INDEX IF NOT EXISTS idx_kg_nodes_owner ON kg_nodes (owner_id);
        CREATE INDEX IF NOT EXISTS idx_kg_nodes_tenant ON kg_nodes (tenant_id);
        """;

    private readonly string _connectionString;
    private readonly ILogger<PostgreSqlGraphStore> _logger;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlGraphStore"/> class.
    /// </summary>
    /// <param name="configMonitor">Application configuration for connection string.</param>
    /// <param name="logger">Logger for recording graph operations.</param>
    public PostgreSqlGraphStore(
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<PostgreSqlGraphStore> logger)
    {
        ArgumentNullException.ThrowIfNull(configMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionString = configMonitor.CurrentValue.AI.Rag.GraphRag.ConnectionString
            ?? throw new InvalidOperationException(
                "GraphRag.ConnectionString must be configured when using the 'postgresql' graph provider.");
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AddNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("kg.postgresql.add_nodes");
        await using var conn = await OpenConnectionAsync(cancellationToken);

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO kg_nodes
                    (id, name, type, properties, chunk_ids, provenance, owner_id, tenant_id, created_at, expires_at)
                VALUES
                    (@id, @name, @type, @props::jsonb, @chunks, @prov::jsonb, @owner_id, @tenant_id, @created_at, @expires_at)
                ON CONFLICT (id) DO UPDATE SET
                    properties = kg_nodes.properties || @props::jsonb,
                    chunk_ids = ARRAY(SELECT DISTINCT unnest(kg_nodes.chunk_ids || @chunks)),
                    provenance = COALESCE(@prov::jsonb, kg_nodes.provenance),
                    owner_id = COALESCE(@owner_id, kg_nodes.owner_id),
                    tenant_id = COALESCE(@tenant_id, kg_nodes.tenant_id),
                    created_at = COALESCE(kg_nodes.created_at, @created_at),
                    expires_at = @expires_at
                """, conn);

            cmd.Parameters.AddWithValue("id", node.Id);
            cmd.Parameters.AddWithValue("name", node.Name);
            cmd.Parameters.AddWithValue("type", node.Type);
            cmd.Parameters.AddWithValue("props", JsonSerializer.Serialize(node.Properties, JsonOptions));
            cmd.Parameters.AddWithValue("chunks", node.ChunkIds.ToArray());
            cmd.Parameters.AddWithValue("prov",
                node.Provenance is not null
                    ? JsonSerializer.Serialize(node.Provenance, JsonOptions)
                    : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("owner_id", (object?)node.OwnerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("tenant_id", (object?)node.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("created_at", (object?)node.CreatedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("expires_at", (object?)node.ExpiresAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogDebug("PostgreSQL: added/merged {Count} nodes", nodes.Count);
    }

    /// <inheritdoc />
    public async Task AddEdgesAsync(
        IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("kg.postgresql.add_edges");
        await using var conn = await OpenConnectionAsync(cancellationToken);

        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO kg_edges
                    (id, source_node_id, target_node_id, predicate, properties, chunk_id, provenance, owner_id, tenant_id, created_at, expires_at)
                VALUES
                    (@id, @source, @target, @pred, @props::jsonb, @chunk, @prov::jsonb, @owner_id, @tenant_id, @created_at, @expires_at)
                ON CONFLICT (id) DO NOTHING
                """, conn);

            cmd.Parameters.AddWithValue("id", edge.Id);
            cmd.Parameters.AddWithValue("source", edge.SourceNodeId);
            cmd.Parameters.AddWithValue("target", edge.TargetNodeId);
            cmd.Parameters.AddWithValue("pred", edge.Predicate);
            cmd.Parameters.AddWithValue("props", JsonSerializer.Serialize(edge.Properties, JsonOptions));
            cmd.Parameters.AddWithValue("chunk", edge.ChunkId);
            cmd.Parameters.AddWithValue("prov",
                edge.Provenance is not null
                    ? JsonSerializer.Serialize(edge.Provenance, JsonOptions)
                    : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("owner_id", (object?)edge.OwnerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("tenant_id", (object?)edge.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("created_at", (object?)edge.CreatedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("expires_at", (object?)edge.ExpiresAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogDebug("PostgreSQL: added {Count} edges", edges.Count);
    }

    /// <inheritdoc />
    public async Task<GraphNode?> GetNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, type, properties, chunk_ids, provenance, owner_id, tenant_id, created_at, expires_at " +
            "FROM kg_nodes WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", nodeId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNode(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        string nodeId,
        int maxDepth = 1,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("kg.postgresql.get_neighbors");
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            WITH RECURSIVE neighbors AS (
                SELECT source_node_id AS neighbor_id, 1 AS depth FROM kg_edges WHERE target_node_id = @id
                UNION
                SELECT target_node_id AS neighbor_id, 1 AS depth FROM kg_edges WHERE source_node_id = @id
                UNION
                SELECT CASE WHEN e.source_node_id = n.neighbor_id THEN e.target_node_id ELSE e.source_node_id END,
                       n.depth + 1
                FROM neighbors n
                JOIN kg_edges e ON e.source_node_id = n.neighbor_id OR e.target_node_id = n.neighbor_id
                WHERE n.depth < @maxDepth
                  AND CASE WHEN e.source_node_id = n.neighbor_id THEN e.target_node_id ELSE e.source_node_id END != @id
            )
            SELECT DISTINCT nd.id, nd.name, nd.type, nd.properties, nd.chunk_ids, nd.provenance,
                   nd.owner_id, nd.tenant_id, nd.created_at, nd.expires_at
            FROM neighbors nb
            JOIN kg_nodes nd ON nd.id = nb.neighbor_id
            """, conn);

        cmd.Parameters.AddWithValue("id", nodeId);
        cmd.Parameters.AddWithValue("maxDepth", maxDepth);

        var results = new List<GraphNode>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadNode(reader));

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphTriplet>> GetTripletsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (nodeIds.Count == 0) return [];

        await using var conn = await OpenConnectionAsync(cancellationToken);
        // Column layout (offsets used by ReadEdgeAt/ReadNodeAt below):
        //   edge   0-10  (11 cols), source 11-20 (10 cols), target 21-30 (10 cols)
        await using var cmd = new NpgsqlCommand("""
            SELECT e.id, e.source_node_id, e.target_node_id, e.predicate, e.properties, e.chunk_id, e.provenance,
                   e.owner_id, e.tenant_id, e.created_at, e.expires_at,
                   s.id, s.name, s.type, s.properties, s.chunk_ids, s.provenance,
                   s.owner_id, s.tenant_id, s.created_at, s.expires_at,
                   t.id, t.name, t.type, t.properties, t.chunk_ids, t.provenance,
                   t.owner_id, t.tenant_id, t.created_at, t.expires_at
            FROM kg_edges e
            JOIN kg_nodes s ON s.id = e.source_node_id
            JOIN kg_nodes t ON t.id = e.target_node_id
            WHERE e.source_node_id = ANY(@ids) OR e.target_node_id = ANY(@ids)
            """, conn);

        cmd.Parameters.AddWithValue("ids", nodeIds.ToArray());

        var triplets = new List<GraphTriplet>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            triplets.Add(new GraphTriplet
            {
                Edge = ReadEdgeAt(reader, 0),
                Source = ReadNodeAt(reader, 11),
                Target = ReadNodeAt(reader, 21)
            });
        }

        return triplets;
    }

    /// <inheritdoc />
    public async Task<bool> NodeExistsAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM kg_nodes WHERE id = @id LIMIT 1", conn);
        cmd.Parameters.AddWithValue("id", nodeId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <inheritdoc />
    public async Task DeleteNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var edgeCmd = new NpgsqlCommand(
            "DELETE FROM kg_edges WHERE source_node_id = @id OR target_node_id = @id", conn);
        edgeCmd.Parameters.AddWithValue("id", nodeId);
        await edgeCmd.ExecuteNonQueryAsync(cancellationToken);

        await using var nodeCmd = new NpgsqlCommand(
            "DELETE FROM kg_nodes WHERE id = @id", conn);
        nodeCmd.Parameters.AddWithValue("id", nodeId);
        await nodeCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteEdgeAsync(
        string edgeId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM kg_edges WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", edgeId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<NodeDeletionResult> DeleteNodesAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (nodeIds.Count == 0) return NodeDeletionResult.Empty;

        using var activity = ActivitySource.StartActivity("kg.postgresql.delete_nodes");
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // Delete connected edges first, capturing their ids for feedback-weight cleanup.
        var deletedEdgeIds = new List<string>();
        await using (var edgeCmd = new NpgsqlCommand(
            "DELETE FROM kg_edges WHERE source_node_id = ANY(@ids) OR target_node_id = ANY(@ids) RETURNING id",
            conn, tx))
        {
            edgeCmd.Parameters.AddWithValue("ids", nodeIds.ToArray());
            await using var reader = await edgeCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                deletedEdgeIds.Add(reader.GetString(0));
        }

        // Then the nodes, RETURNING the ids actually removed so audits record actuals.
        var deletedNodeIds = new List<string>();
        await using (var nodeCmd = new NpgsqlCommand(
            "DELETE FROM kg_nodes WHERE id = ANY(@ids) RETURNING id", conn, tx))
        {
            nodeCmd.Parameters.AddWithValue("ids", nodeIds.ToArray());
            await using var reader = await nodeCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                deletedNodeIds.Add(reader.GetString(0));
        }

        await tx.CommitAsync(cancellationToken);

        _logger.LogDebug(
            "PostgreSQL: deleted {NodeCount} of {Requested} nodes and {EdgeCount} connected edges",
            deletedNodeIds.Count, nodeIds.Count, deletedEdgeIds.Count);

        return new NodeDeletionResult
        {
            DeletedNodeIds = deletedNodeIds,
            DeletedEdgeIds = deletedEdgeIds
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> DeleteEdgesByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("kg.postgresql.delete_edges_by_owner");
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM kg_edges WHERE owner_id = @ownerId RETURNING id", conn);
        cmd.Parameters.AddWithValue("ownerId", ownerId);

        var deleted = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            deleted.Add(reader.GetString(0));

        _logger.LogDebug("PostgreSQL: deleted {Count} edges owned by {OwnerId}", deleted.Count, ownerId);
        return deleted;
    }

    /// <inheritdoc />
    public async Task<int> GetNodeCountAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM kg_nodes", conn);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <inheritdoc />
    public async Task<int> GetEdgeCountAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM kg_edges", conn);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNodesByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, type, properties, chunk_ids, provenance, owner_id, tenant_id, created_at, expires_at " +
            "FROM kg_nodes WHERE owner_id = @ownerId", conn);
        cmd.Parameters.AddWithValue("ownerId", ownerId);

        var results = new List<GraphNode>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadNode(reader));

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, type, properties, chunk_ids, provenance, owner_id, tenant_id, created_at, expires_at " +
            "FROM kg_nodes", conn);

        var results = new List<GraphNode>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadNode(reader));

        return results;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        return conn;
    }

    /// <summary>
    /// Creates the <c>kg_nodes</c>/<c>kg_edges</c> tables (with the full isolation/temporal column
    /// set) if they do not already exist. Runs once per store instance — the backend is a singleton —
    /// guarded so concurrent callers initialize the schema exactly once.
    /// </summary>
    private async Task EnsureSchemaAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;

            // Serialize DDL across processes/replicas: concurrent CREATE TABLE/INDEX IF NOT EXISTS
            // on a fresh database can race and throw ("tuple concurrently updated" / duplicate
            // object). A transaction-scoped advisory lock (auto-released on commit) makes first-use
            // safe in a scaled-out deployment; the in-process semaphore + flag handle the common case.
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", conn, tx))
            {
                lockCmd.Parameters.AddWithValue("key", 0x6B675F736368656DL); // "kg_schem"
                await lockCmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = new NpgsqlCommand(SchemaDdl, conn, tx))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static GraphNode ReadNode(NpgsqlDataReader reader) => ReadNodeAt(reader, 0);

    /// <summary>
    /// Reads a node from 10 consecutive columns starting at <paramref name="o"/>, in the order
    /// id, name, type, properties, chunk_ids, provenance, owner_id, tenant_id, created_at, expires_at.
    /// </summary>
    private static GraphNode ReadNodeAt(NpgsqlDataReader r, int o)
    {
        return new GraphNode
        {
            Id = r.GetString(o),
            Name = r.GetString(o + 1),
            Type = r.GetString(o + 2),
            Properties = DeserializeProps(r.GetString(o + 3)),
            ChunkIds = (string[])r.GetValue(o + 4),
            Provenance = r.IsDBNull(o + 5)
                ? null
                : JsonSerializer.Deserialize<ProvenanceStamp>(r.GetString(o + 5), JsonOptions),
            OwnerId = r.IsDBNull(o + 6) ? null : r.GetString(o + 6),
            TenantId = r.IsDBNull(o + 7) ? null : r.GetString(o + 7),
            CreatedAt = r.IsDBNull(o + 8) ? null : r.GetFieldValue<DateTimeOffset>(o + 8),
            ExpiresAt = r.IsDBNull(o + 9) ? null : r.GetFieldValue<DateTimeOffset>(o + 9)
        };
    }

    /// <summary>
    /// Reads an edge from 11 consecutive columns starting at <paramref name="o"/>, in the order
    /// id, source_node_id, target_node_id, predicate, properties, chunk_id, provenance, owner_id,
    /// tenant_id, created_at, expires_at.
    /// </summary>
    private static GraphEdge ReadEdgeAt(NpgsqlDataReader r, int o)
    {
        return new GraphEdge
        {
            Id = r.GetString(o),
            SourceNodeId = r.GetString(o + 1),
            TargetNodeId = r.GetString(o + 2),
            Predicate = r.GetString(o + 3),
            Properties = DeserializeProps(r.GetString(o + 4)),
            ChunkId = r.GetString(o + 5),
            Provenance = r.IsDBNull(o + 6)
                ? null
                : JsonSerializer.Deserialize<ProvenanceStamp>(r.GetString(o + 6), JsonOptions),
            OwnerId = r.IsDBNull(o + 7) ? null : r.GetString(o + 7),
            TenantId = r.IsDBNull(o + 8) ? null : r.GetString(o + 8),
            CreatedAt = r.IsDBNull(o + 9) ? null : r.GetFieldValue<DateTimeOffset>(o + 9),
            ExpiresAt = r.IsDBNull(o + 10) ? null : r.GetFieldValue<DateTimeOffset>(o + 10)
        };
    }

    private static IReadOnlyDictionary<string, string> DeserializeProps(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
            ?? new Dictionary<string, string>();
    }
}
