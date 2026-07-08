using System.Text.Json;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.GraphRag;

public sealed partial class KuzuGraphBackend
{
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
    public async Task<NodeDeletionResult> DeleteNodesAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (nodeIds.Count == 0) return NodeDeletionResult.Empty;

        await PopulateTempTableAsync(nodeIds, cancellationToken).ConfigureAwait(false);

        // Capture connected edge ids BEFORE deleting so feedback-weight cleanup and the
        // erasure receipt reflect what was actually removed.
        var deletedEdgeIds = new List<string>();
        using (var selectCmd = _connection.CreateCommand())
        {
            selectCmd.CommandText = """
                SELECT id FROM Edges
                WHERE source_node_id IN (SELECT id FROM _TempIds)
                   OR target_node_id  IN (SELECT id FROM _TempIds)
                """;
            using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                deletedEdgeIds.Add(reader.GetString(0));
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM Edges
                WHERE source_node_id IN (SELECT id FROM _TempIds)
                   OR target_node_id  IN (SELECT id FROM _TempIds)
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM CommunityAssignments WHERE node_id IN (SELECT id FROM _TempIds)";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int nodesDeleted;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM Nodes WHERE id IN (SELECT id FROM _TempIds)";
            nodesDeleted = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Deleted {NodeCount} of {Requested} nodes and {EdgeCount} connected edges",
            nodesDeleted, nodeIds.Count, deletedEdgeIds.Count);

        return new NodeDeletionResult
        {
            NodesDeleted = nodesDeleted,
            DeletedEdgeIds = deletedEdgeIds
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> DeleteEdgesByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var deleted = new List<string>();
        using (var selectCmd = _connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT id FROM Edges WHERE owner_id = @owner";
            selectCmd.Parameters.AddWithValue("@owner", ownerId);
            using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                deleted.Add(reader.GetString(0));
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM Edges WHERE owner_id = @owner";
            cmd.Parameters.AddWithValue("@owner", ownerId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Deleted {Count} edges owned by {OwnerId}", deleted.Count, ownerId);
        return deleted;
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
}
