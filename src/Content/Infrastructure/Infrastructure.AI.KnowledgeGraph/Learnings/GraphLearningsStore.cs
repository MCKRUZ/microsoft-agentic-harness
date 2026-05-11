using System.Globalization;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Learnings;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Learnings;

/// <summary>
/// Graph-backed implementation of <see cref="ILearningsStore"/> using deterministic node IDs
/// and synthetic index nodes for efficient scope-hierarchy search.
/// Registered with keyed DI key <c>"graph"</c>.
/// </summary>
public sealed class GraphLearningsStore : ILearningsStore
{
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly ILogger<GraphLearningsStore> _logger;

    private const string NodePrefix = "learning:";
    private const string IndexPrefix = "learningindex:";
    private const string NodeType = "LearningEntry";
    private const string IndexType = "LearningIndex";
    private const string EdgePredicate = "has_learning";
    private const string ChunkId = "learningindex";

    public GraphLearningsStore(
        IKnowledgeGraphStore graphStore,
        ILogger<GraphLearningsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(logger);

        _graphStore = graphStore;
        _logger = logger;
    }

    public async Task<Result> SaveAsync(LearningEntry learning, CancellationToken ct)
    {
        try
        {
            var nodeId = ToNodeId(learning.LearningId);
            var node = new GraphNode
            {
                Id = nodeId,
                Name = $"Learning: {learning.Content[..Math.Min(50, learning.Content.Length)]}",
                Type = NodeType,
                Properties = SerializeProperties(learning)
            };

            await _graphStore.AddNodesAsync([node], ct);
            await CreateIndexEdgesAsync(nodeId, learning.Scope, ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save learning {LearningId}", learning.LearningId);
            return Result.Fail($"Failed to save learning: {ex.Message}");
        }
    }

    public async Task<Result<LearningEntry?>> GetAsync(Guid learningId, CancellationToken ct)
    {
        try
        {
            var node = await _graphStore.GetNodeAsync(ToNodeId(learningId), ct);
            if (node is null)
                return Result<LearningEntry?>.Success(null);

            var entry = DeserializeLearningEntry(learningId, node);
            if (entry is null || entry.IsDeleted)
                return Result<LearningEntry?>.Success(null);

            return Result<LearningEntry?>.Success(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get learning {LearningId}", learningId);
            return Result<LearningEntry?>.Fail($"Failed to get learning: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<LearningEntry>>> SearchAsync(LearningSearchCriteria criteria, CancellationToken ct)
    {
        try
        {
            var candidateNodes = new Dictionary<string, GraphNode>();

            if (criteria.Scope is null)
            {
                // Null scope = search all learnings. O(N) full scan — acceptable for pruning but not high-frequency queries.
                var allNodes = await _graphStore.GetAllNodesAsync(ct);
                foreach (var n in allNodes.Where(n => n.Type == NodeType))
                    candidateNodes.TryAdd(n.Id, n);
            }
            else
            {
                if (criteria.Scope.AgentId is not null)
                    await CollectIndexNeighborsAsync($"{IndexPrefix}agent:{criteria.Scope.AgentId}".ToLowerInvariant(), candidateNodes, ct);

                if (criteria.Scope.TeamId is not null)
                    await CollectIndexNeighborsAsync($"{IndexPrefix}team:{criteria.Scope.TeamId}".ToLowerInvariant(), candidateNodes, ct);

                // Global learnings are always included in scoped searches (scope hierarchy).
                await CollectIndexNeighborsAsync($"{IndexPrefix}global", candidateNodes, ct);
            }

            var entries = new List<LearningEntry>();
            foreach (var node in candidateNodes.Values)
            {
                if (node.Properties.GetValueOrDefault("IsDeleted", "false") == "true")
                    continue;

                var id = ExtractLearningId(node.Id);
                if (id is null) continue;

                var entry = DeserializeLearningEntry(id.Value, node);
                if (entry is null) continue;

                if (criteria.Category is not null && entry.Category != criteria.Category)
                    continue;
                if (criteria.MinFeedbackWeight is not null && entry.FeedbackWeight < criteria.MinFeedbackWeight)
                    continue;
                if (criteria.CreatedAfter is not null && entry.CreatedAt < criteria.CreatedAfter)
                    continue;
                if (criteria.CreatedBefore is not null && entry.CreatedAt > criteria.CreatedBefore)
                    continue;

                entries.Add(entry);
            }

            return Result<IReadOnlyList<LearningEntry>>.Success(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search learnings");
            return Result<IReadOnlyList<LearningEntry>>.Fail($"Failed to search learnings: {ex.Message}");
        }
    }

    // Scope is immutable after creation — index edges are not updated here.
    public async Task<Result> UpdateAsync(LearningEntry learning, CancellationToken ct)
    {
        try
        {
            var nodeId = ToNodeId(learning.LearningId);
            var existing = await _graphStore.GetNodeAsync(nodeId, ct);
            if (existing is null)
                return Result.Fail("Learning not found");

            var node = new GraphNode
            {
                Id = nodeId,
                Name = $"Learning: {learning.Content[..Math.Min(50, learning.Content.Length)]}",
                Type = NodeType,
                Properties = SerializeProperties(learning)
            };

            await _graphStore.AddNodesAsync([node], ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update learning {LearningId}", learning.LearningId);
            return Result.Fail($"Failed to update learning: {ex.Message}");
        }
    }

    public async Task<Result> SoftDeleteAsync(Guid learningId, string reason, CancellationToken ct)
    {
        try
        {
            var nodeId = ToNodeId(learningId);
            var existing = await _graphStore.GetNodeAsync(nodeId, ct);
            if (existing is null)
                return Result.Fail("Learning not found");

            var updatedProps = existing.Properties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            updatedProps["IsDeleted"] = "true";
            updatedProps["DeleteReason"] = reason;

            var updatedNode = new GraphNode
            {
                Id = nodeId,
                Name = existing.Name,
                Type = existing.Type,
                Properties = updatedProps
            };

            await _graphStore.AddNodesAsync([updatedNode], ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to soft-delete learning {LearningId}", learningId);
            return Result.Fail($"Failed to soft-delete learning: {ex.Message}");
        }
    }

    private static string ToNodeId(Guid learningId) =>
        $"{NodePrefix}{learningId}".ToLowerInvariant();

    private static Guid? ExtractLearningId(string nodeId)
    {
        if (!nodeId.StartsWith(NodePrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return Guid.TryParse(nodeId[NodePrefix.Length..], out var id) ? id : null;
    }

    private async Task CreateIndexEdgesAsync(string nodeId, LearningScope scope, CancellationToken ct)
    {
        var indexNodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        if (scope.AgentId is not null)
        {
            var indexId = $"{IndexPrefix}agent:{scope.AgentId}".ToLowerInvariant();
            indexNodes.Add(new GraphNode { Id = indexId, Name = $"Agent:{scope.AgentId}", Type = IndexType });
            edges.Add(CreateEdge(indexId, nodeId));
        }

        if (scope.TeamId is not null)
        {
            var indexId = $"{IndexPrefix}team:{scope.TeamId}".ToLowerInvariant();
            indexNodes.Add(new GraphNode { Id = indexId, Name = $"Team:{scope.TeamId}", Type = IndexType });
            edges.Add(CreateEdge(indexId, nodeId));
        }

        if (scope.IsGlobal)
        {
            var indexId = $"{IndexPrefix}global";
            indexNodes.Add(new GraphNode { Id = indexId, Name = "Global", Type = IndexType });
            edges.Add(CreateEdge(indexId, nodeId));
        }

        if (indexNodes.Count > 0)
            await _graphStore.AddNodesAsync(indexNodes, ct);
        if (edges.Count > 0)
            await _graphStore.AddEdgesAsync(edges, ct);
    }

    private static GraphEdge CreateEdge(string indexId, string nodeId) => new()
    {
        Id = $"edge:{indexId}:{nodeId}",
        SourceNodeId = indexId,
        TargetNodeId = nodeId,
        Predicate = EdgePredicate,
        ChunkId = ChunkId
    };

    private async Task CollectIndexNeighborsAsync(string indexNodeId, Dictionary<string, GraphNode> candidates, CancellationToken ct)
    {
        if (!await _graphStore.NodeExistsAsync(indexNodeId, ct))
            return;

        var neighbors = await _graphStore.GetNeighborsAsync(indexNodeId, maxDepth: 1, ct);
        foreach (var neighbor in neighbors.Where(n => n.Type == NodeType))
            candidates.TryAdd(neighbor.Id, neighbor);
    }

    private static Dictionary<string, string> SerializeProperties(LearningEntry entry) => new()
    {
        ["Content"] = entry.Content,
        ["Category"] = entry.Category.ToString(),
        ["DecayClass"] = entry.DecayClass.ToString(),
        ["FeedbackWeight"] = entry.FeedbackWeight.ToString("F6", CultureInfo.InvariantCulture),
        ["UpdateCount"] = entry.UpdateCount.ToString(CultureInfo.InvariantCulture),
        ["CreatedAt"] = entry.CreatedAt.ToString("O"),
        ["LastAccessedAt"] = entry.LastAccessedAt?.ToString("O") ?? "",
        ["LastReinforcedAt"] = entry.LastReinforcedAt?.ToString("O") ?? "",
        ["SourceType"] = entry.Source.SourceType.ToString(),
        ["SourceId"] = entry.Source.SourceId,
        ["SourceDescription"] = entry.Source.SourceDescription,
        ["ProvenancePipeline"] = entry.Provenance.OriginPipeline,
        ["ProvenanceTask"] = entry.Provenance.OriginTask,
        ["ProvenanceTimestamp"] = entry.Provenance.OriginTimestamp.ToString("O"),
        ["ProvenanceConfidence"] = entry.Provenance.Confidence.ToString("F4", CultureInfo.InvariantCulture),
        ["ScopeAgentId"] = entry.Scope.AgentId ?? "",
        ["ScopeTeamId"] = entry.Scope.TeamId ?? "",
        ["ScopeIsGlobal"] = entry.Scope.IsGlobal.ToString().ToLowerInvariant(),
        ["IsDeleted"] = entry.IsDeleted.ToString().ToLowerInvariant(),
        ["DeleteReason"] = entry.DeleteReason ?? ""
    };

    private LearningEntry? DeserializeLearningEntry(Guid learningId, GraphNode node)
    {
        var props = node.Properties;

        if (!props.ContainsKey("Content") || !props.ContainsKey("Category"))
        {
            _logger.LogWarning("Skipping graph node {NodeId}: missing required properties", node.Id);
            return null;
        }

        return new LearningEntry
        {
            LearningId = learningId,
            Content = props.GetValueOrDefault("Content", ""),
            Category = Enum.TryParse<LearningCategory>(props.GetValueOrDefault("Category", ""), out var cat)
                ? cat : LearningCategory.DomainKnowledge,
            DecayClass = Enum.TryParse<DecayClass>(props.GetValueOrDefault("DecayClass", ""), out var dc)
                ? dc : DecayClass.Stable,
            FeedbackWeight = double.TryParse(props.GetValueOrDefault("FeedbackWeight", "1.0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fw)
                ? fw : 1.0,
            UpdateCount = int.TryParse(props.GetValueOrDefault("UpdateCount", "0"), out var uc)
                ? uc : 0,
            CreatedAt = DateTimeOffset.TryParse(props.GetValueOrDefault("CreatedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ca)
                ? ca : DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.TryParse(props.GetValueOrDefault("LastAccessedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var la)
                ? la : null,
            LastReinforcedAt = DateTimeOffset.TryParse(props.GetValueOrDefault("LastReinforcedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var lr)
                ? lr : null,
            Source = new LearningSource
            {
                SourceType = Enum.TryParse<LearningSourceType>(props.GetValueOrDefault("SourceType", ""), out var st)
                    ? st : LearningSourceType.ManualEntry,
                SourceId = props.GetValueOrDefault("SourceId", ""),
                SourceDescription = props.GetValueOrDefault("SourceDescription", "")
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = props.GetValueOrDefault("ProvenancePipeline", ""),
                OriginTask = props.GetValueOrDefault("ProvenanceTask", ""),
                OriginTimestamp = DateTimeOffset.TryParse(props.GetValueOrDefault("ProvenanceTimestamp", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out var pt)
                    ? pt : DateTimeOffset.UtcNow,
                Confidence = double.TryParse(props.GetValueOrDefault("ProvenanceConfidence", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var pc)
                    ? pc : 0.0
            },
            Scope = new LearningScope
            {
                AgentId = string.IsNullOrEmpty(props.GetValueOrDefault("ScopeAgentId", "")) ? null : props["ScopeAgentId"],
                TeamId = string.IsNullOrEmpty(props.GetValueOrDefault("ScopeTeamId", "")) ? null : props["ScopeTeamId"],
                IsGlobal = props.GetValueOrDefault("ScopeIsGlobal", "false") == "true"
            },
            IsDeleted = props.GetValueOrDefault("IsDeleted", "false") == "true",
            DeleteReason = string.IsNullOrEmpty(props.GetValueOrDefault("DeleteReason", "")) ? null : props["DeleteReason"]
        };
    }
}
