using System.Text.Json;
using Domain.AI.KnowledgeGraph.Models;

namespace Infrastructure.AI.RAG.GraphRag;

public sealed partial class KuzuGraphBackend
{
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
    public async Task<int> DeleteCommunitiesContainingAsync(
        IReadOnlyCollection<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds.Count == 0)
            return 0;

        var touched = new HashSet<string>(StringComparer.Ordinal);
        using (var read = _connection.CreateCommand())
        {
            read.CommandText = "SELECT id, node_ids_json FROM Communities";
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var members = JsonSerializer.Deserialize<List<string>>(reader.GetString(1), _jsonOptions) ?? [];
                if (members.Any(nodeIds.Contains))
                    touched.Add(reader.GetString(0));
            }
        }

        using (var assigned = _connection.CreateCommand())
        {
            assigned.CommandText = "SELECT DISTINCT community_id, node_id FROM CommunityAssignments";
            using var reader = await assigned.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (nodeIds.Contains(reader.GetString(1)))
                    touched.Add(reader.GetString(0));
            }
        }

        foreach (var communityId in touched)
        {
            using var delete = _connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM CommunityAssignments WHERE community_id = @id;
                DELETE FROM Communities WHERE id = @id;
                """;
            delete.Parameters.AddWithValue("@id", communityId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return touched.Count;
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
}
