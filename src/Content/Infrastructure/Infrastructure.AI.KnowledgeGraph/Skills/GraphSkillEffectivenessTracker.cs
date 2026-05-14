using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Skills;

/// <summary>
/// Tracks skill effectiveness by storing <c>SkillMetric</c> nodes in the knowledge graph.
/// Each node represents a skill + query classification pair with aggregated outcome stats.
/// A synthetic <c>SkillClassification</c> index node links to all metrics for that classification,
/// enabling efficient retrieval via <see cref="IKnowledgeGraphStore.GetNeighborsAsync"/>.
/// </summary>
public sealed class GraphSkillEffectivenessTracker : ISkillEffectivenessTracker
{
    private readonly IKnowledgeGraphStore _graphStore;
    private readonly ILogger<GraphSkillEffectivenessTracker> _logger;

    public GraphSkillEffectivenessTracker(
        IKnowledgeGraphStore graphStore,
        ILogger<GraphSkillEffectivenessTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(logger);
        _graphStore = graphStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordOutcomeAsync(
        string skillId,
        string queryClassification,
        bool succeeded,
        double? qualityScore = null,
        CancellationToken cancellationToken = default)
    {
        var nodeId = $"skillmetric:{skillId}:{queryClassification}".ToLowerInvariant();
        var existing = await _graphStore.GetNodeAsync(nodeId, cancellationToken);

        int successCount = succeeded ? 1 : 0;
        int totalCount = 1;
        double avgQuality = qualityScore ?? 0;

        if (existing is not null)
        {
            var prevSuccess = int.Parse(existing.Properties.GetValueOrDefault("SuccessCount", "0"));
            var prevTotal = int.Parse(existing.Properties.GetValueOrDefault("TotalCount", "0"));
            var prevAvg = double.Parse(existing.Properties.GetValueOrDefault("AverageQuality", "0"));

            successCount = prevSuccess + (succeeded ? 1 : 0);
            totalCount = prevTotal + 1;
            avgQuality = qualityScore.HasValue
                ? (prevAvg * prevTotal + qualityScore.Value) / totalCount
                : prevAvg;
        }

        var node = new GraphNode
        {
            Id = nodeId,
            Name = $"{skillId} ({queryClassification})",
            Type = "SkillMetric",
            Properties = new Dictionary<string, string>
            {
                ["SkillId"] = skillId,
                ["QueryClassification"] = queryClassification,
                ["SuccessCount"] = successCount.ToString(),
                ["TotalCount"] = totalCount.ToString(),
                ["AverageQuality"] = avgQuality.ToString("F4")
            }
        };

        await _graphStore.AddNodesAsync([node], cancellationToken);

        // Ensure classification index node exists and link to metric
        var indexNodeId = $"skillclass:{queryClassification}".ToLowerInvariant();
        var indexNode = new GraphNode
        {
            Id = indexNodeId,
            Name = queryClassification,
            Type = "SkillClassification"
        };
        await _graphStore.AddNodesAsync([indexNode], cancellationToken);

        var edgeId = $"edge:{indexNodeId}:{nodeId}";
        var edge = new GraphEdge
        {
            Id = edgeId,
            SourceNodeId = indexNodeId,
            TargetNodeId = nodeId,
            Predicate = "tracks",
            ChunkId = ""
        };
        await _graphStore.AddEdgesAsync([edge], cancellationToken);

        _logger.LogDebug(
            "Recorded skill outcome: {SkillId}/{Classification} success={Success}",
            skillId, queryClassification, succeeded);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkillEffectivenessRecord>> GetEffectivenessAsync(
        string queryClassification,
        int topN = 5,
        CancellationToken cancellationToken = default)
    {
        var indexNodeId = $"skillclass:{queryClassification}".ToLowerInvariant();
        var neighbors = await _graphStore.GetNeighborsAsync(indexNodeId, maxDepth: 1, cancellationToken);

        var results = new List<SkillEffectivenessRecord>();

        foreach (var node in neighbors.Where(n => n.Type == "SkillMetric"))
        {
            if (node.Properties.GetValueOrDefault("QueryClassification") != queryClassification)
                continue;

            results.Add(new SkillEffectivenessRecord
            {
                SkillId = node.Properties.GetValueOrDefault("SkillId", ""),
                QueryClassification = queryClassification,
                SuccessCount = int.Parse(node.Properties.GetValueOrDefault("SuccessCount", "0")),
                TotalCount = int.Parse(node.Properties.GetValueOrDefault("TotalCount", "0")),
                AverageQuality = double.Parse(node.Properties.GetValueOrDefault("AverageQuality", "0"))
            });
        }

        return results.OrderByDescending(r => r.SuccessRate).Take(topN).ToList();
    }
}
