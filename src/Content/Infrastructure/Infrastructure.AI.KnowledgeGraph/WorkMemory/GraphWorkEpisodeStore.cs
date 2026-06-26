using System.Globalization;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.WorkMemory;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.WorkMemory;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.WorkMemory;

/// <summary>
/// Graph-backed implementation of <see cref="IWorkEpisodeStore"/> using deterministic node IDs and a
/// per-conversation index node for efficient grouped retrieval. Registered with keyed DI key
/// <c>"graph"</c>.
/// </summary>
/// <remarks>
/// Tenant/owner isolation is <strong>not</strong> implemented here — it is inherited from the injected
/// <see cref="IKnowledgeGraphStore"/>, which in production is the tenant-isolating / compliance-aware
/// decorator chain (stamps tenant on write, filters on read). This mirrors <c>GraphLearningsStore</c>.
/// </remarks>
public sealed class GraphWorkEpisodeStore : IWorkEpisodeStore
{
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly ILogger<GraphWorkEpisodeStore> _logger;

    private const string NodePrefix = "workepisode:";
    private const string IndexPrefix = "workepisodeindex:conv:";
    private const string NodeType = "WorkEpisode";
    private const string IndexType = "WorkEpisodeIndex";
    private const string EdgePredicate = "has_episode";
    private const string ChunkId = "workepisodeindex";

    /// <summary>Initializes a new instance of the <see cref="GraphWorkEpisodeStore"/> class.</summary>
    public GraphWorkEpisodeStore(
        IKnowledgeGraphStore graphStore,
        ILogger<GraphWorkEpisodeStore> logger)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(logger);

        _graphStore = graphStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> SaveAsync(WorkEpisode episode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(episode);
        try
        {
            var nodeId = ToNodeId(episode.EpisodeId);
            var node = new GraphNode
            {
                Id = nodeId,
                Name = $"Episode: {episode.ConversationId}#{episode.TurnNumber}",
                Type = NodeType,
                Properties = SerializeProperties(episode)
            };

            await _graphStore.AddNodesAsync([node], ct);
            await CreateIndexEdgeAsync(nodeId, episode.ConversationId, ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save work episode {EpisodeId}", episode.EpisodeId);
            return Result.Fail($"Failed to save work episode: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<WorkEpisode?>> GetAsync(Guid episodeId, CancellationToken ct)
    {
        try
        {
            var node = await _graphStore.GetNodeAsync(ToNodeId(episodeId), ct);
            if (node is null)
                return Result<WorkEpisode?>.Success(null);

            return Result<WorkEpisode?>.Success(Deserialize(episodeId, node));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get work episode {EpisodeId}", episodeId);
            return Result<WorkEpisode?>.Fail($"Failed to get work episode: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<WorkEpisode>>> SearchAsync(WorkEpisodeSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        try
        {
            var candidates = new Dictionary<string, GraphNode>();

            if (criteria.ConversationId is not null)
            {
                await CollectIndexNeighborsAsync(ToIndexId(criteria.ConversationId), candidates, ct);
            }
            else
            {
                // No conversation filter = scan all episode nodes. O(N); acceptable for the overnight
                // synthesis pass, which is the only cross-conversation caller.
                var allNodes = await _graphStore.GetAllNodesAsync(ct);
                foreach (var n in allNodes.Where(n => n.Type == NodeType))
                    candidates.TryAdd(n.Id, n);
            }

            var episodes = new List<WorkEpisode>();
            foreach (var node in candidates.Values)
            {
                var id = ExtractEpisodeId(node.Id);
                if (id is null) continue;

                var episode = Deserialize(id.Value, node);
                if (episode is null) continue;

                if (criteria.Outcome is not null && episode.Outcome != criteria.Outcome)
                    continue;
                if (criteria.CreatedAfter is not null && episode.CreatedAt < criteria.CreatedAfter)
                    continue;
                if (criteria.CreatedBefore is not null && episode.CreatedAt > criteria.CreatedBefore)
                    continue;

                episodes.Add(episode);
            }

            IEnumerable<WorkEpisode> ordered = episodes.OrderByDescending(e => e.CreatedAt);
            if (criteria.Limit is { } limit)
                ordered = ordered.Take(limit);

            return Result<IReadOnlyList<WorkEpisode>>.Success(ordered.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search work episodes");
            return Result<IReadOnlyList<WorkEpisode>>.Fail($"Failed to search work episodes: {ex.Message}");
        }
    }

    private static string ToNodeId(Guid episodeId) => $"{NodePrefix}{episodeId}".ToLowerInvariant();

    private static string ToIndexId(string conversationId) =>
        $"{IndexPrefix}{conversationId}".ToLowerInvariant();

    private static Guid? ExtractEpisodeId(string nodeId)
    {
        if (!nodeId.StartsWith(NodePrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return Guid.TryParse(nodeId[NodePrefix.Length..], out var id) ? id : null;
    }

    private async Task CreateIndexEdgeAsync(string nodeId, string conversationId, CancellationToken ct)
    {
        var indexId = ToIndexId(conversationId);
        var indexNode = new GraphNode { Id = indexId, Name = $"Conversation:{conversationId}", Type = IndexType };

        await _graphStore.AddNodesAsync([indexNode], ct);
        await _graphStore.AddEdgesAsync(
            [new GraphEdge
            {
                Id = $"edge:{indexId}:{nodeId}",
                SourceNodeId = indexId,
                TargetNodeId = nodeId,
                Predicate = EdgePredicate,
                ChunkId = ChunkId
            }],
            ct);
    }

    private async Task CollectIndexNeighborsAsync(string indexNodeId, Dictionary<string, GraphNode> candidates, CancellationToken ct)
    {
        if (!await _graphStore.NodeExistsAsync(indexNodeId, ct))
            return;

        var neighbors = await _graphStore.GetNeighborsAsync(indexNodeId, maxDepth: 1, ct);
        foreach (var neighbor in neighbors.Where(n => n.Type == NodeType))
            candidates.TryAdd(neighbor.Id, neighbor);
    }

    private static Dictionary<string, string> SerializeProperties(WorkEpisode episode) => new()
    {
        ["AgentId"] = episode.AgentId,
        ["ConversationId"] = episode.ConversationId,
        ["TurnNumber"] = episode.TurnNumber.ToString(CultureInfo.InvariantCulture),
        ["UserMessage"] = episode.UserMessage,
        ["ResponseSummary"] = episode.ResponseSummary,
        ["Outcome"] = episode.Outcome.ToString(),
        ["InputTokens"] = episode.InputTokens.ToString(CultureInfo.InvariantCulture),
        ["OutputTokens"] = episode.OutputTokens.ToString(CultureInfo.InvariantCulture),
        ["CreatedAt"] = episode.CreatedAt.ToString("O")
    };

    private WorkEpisode? Deserialize(Guid episodeId, GraphNode node)
    {
        var props = node.Properties;

        if (!props.ContainsKey("ConversationId") || !props.ContainsKey("Outcome"))
        {
            _logger.LogWarning("Skipping graph node {NodeId}: missing required work-episode properties", node.Id);
            return null;
        }

        return new WorkEpisode
        {
            EpisodeId = episodeId,
            AgentId = props.GetValueOrDefault("AgentId", ""),
            ConversationId = props.GetValueOrDefault("ConversationId", ""),
            TurnNumber = int.TryParse(props.GetValueOrDefault("TurnNumber", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tn) ? tn : 0,
            UserMessage = props.GetValueOrDefault("UserMessage", ""),
            ResponseSummary = props.GetValueOrDefault("ResponseSummary", ""),
            Outcome = Enum.TryParse<EpisodeOutcome>(props.GetValueOrDefault("Outcome", ""), out var oc) ? oc : EpisodeOutcome.Success,
            InputTokens = int.TryParse(props.GetValueOrDefault("InputTokens", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var it) ? it : 0,
            OutputTokens = int.TryParse(props.GetValueOrDefault("OutputTokens", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ot) ? ot : 0,
            CreatedAt = DateTimeOffset.TryParse(props.GetValueOrDefault("CreatedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ca) ? ca : DateTimeOffset.UtcNow
        };
    }
}
