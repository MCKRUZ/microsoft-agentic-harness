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

    private readonly string _connectionString;
    private readonly ILogger<PostgreSqlGraphStore> _logger;

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
                INSERT INTO kg_nodes (id, name, type, properties, chunk_ids, provenance)
                VALUES (@id, @name, @type, @props::jsonb, @chunks, @prov::jsonb)
                ON CONFLICT (id) DO UPDATE SET
                    properties = kg_nodes.properties || @props::jsonb,
                    chunk_ids = ARRAY(SELECT DISTINCT unnest(kg_nodes.chunk_ids || @chunks)),
                    provenance = COALESCE(@prov::jsonb, kg_nodes.provenance)
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
                INSERT INTO kg_edges (id, source_node_id, target_node_id, predicate, properties, chunk_id, provenance)
                VALUES (@id, @source, @target, @pred, @props::jsonb, @chunk, @prov::jsonb)
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
            "SELECT id, name, type, properties, chunk_ids, provenance FROM kg_nodes WHERE id = @id", conn);
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
            SELECT DISTINCT nd.id, nd.name, nd.type, nd.properties, nd.chunk_ids, nd.provenance
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
        await using var cmd = new NpgsqlCommand("""
            SELECT e.id AS eid, e.source_node_id, e.target_node_id, e.predicate, e.properties AS eprops, e.chunk_id, e.provenance AS eprov,
                   s.id AS sid, s.name AS sname, s.type AS stype, s.properties AS sprops, s.chunk_ids AS schunks, s.provenance AS sprov,
                   t.id AS tid, t.name AS tname, t.type AS ttype, t.properties AS tprops, t.chunk_ids AS tchunks, t.provenance AS tprov
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
                Source = new GraphNode
                {
                    Id = reader.GetString(7), Name = reader.GetString(8),
                    Type = reader.GetString(9),
                    Properties = DeserializeProps(reader.GetString(10)),
                    ChunkIds = (string[])reader.GetValue(11),
                    Provenance = reader.IsDBNull(12)
                        ? null
                        : JsonSerializer.Deserialize<ProvenanceStamp>(reader.GetString(12), JsonOptions)
                },
                Edge = new GraphEdge
                {
                    Id = reader.GetString(0), SourceNodeId = reader.GetString(1),
                    TargetNodeId = reader.GetString(2), Predicate = reader.GetString(3),
                    Properties = DeserializeProps(reader.GetString(4)),
                    ChunkId = reader.GetString(5),
                    Provenance = reader.IsDBNull(6)
                        ? null
                        : JsonSerializer.Deserialize<ProvenanceStamp>(reader.GetString(6), JsonOptions)
                },
                Target = new GraphNode
                {
                    Id = reader.GetString(13), Name = reader.GetString(14),
                    Type = reader.GetString(15),
                    Properties = DeserializeProps(reader.GetString(16)),
                    ChunkIds = (string[])reader.GetValue(17),
                    Provenance = reader.IsDBNull(18)
                        ? null
                        : JsonSerializer.Deserialize<ProvenanceStamp>(reader.GetString(18), JsonOptions)
                }
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

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static GraphNode ReadNode(NpgsqlDataReader reader)
    {
        return new GraphNode
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Type = reader.GetString(2),
            Properties = DeserializeProps(reader.GetString(3)),
            ChunkIds = (string[])reader.GetValue(4),
            Provenance = reader.IsDBNull(5)
                ? null
                : JsonSerializer.Deserialize<ProvenanceStamp>(reader.GetString(5), JsonOptions)
        };
    }

    private static IReadOnlyDictionary<string, string> DeserializeProps(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
            ?? new Dictionary<string, string>();
    }
}
