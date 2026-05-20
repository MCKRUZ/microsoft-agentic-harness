using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Blends reranked retrieval scores with feedback weights from the knowledge graph,
/// then persists the adjusted weights back to the graph backend for future retrieval
/// improvement.
/// </summary>
/// <remarks>
/// Blending formula:
/// <c>adjustedScore = (1 - alpha) * rerankScore + alpha * avgNodeWeight</c>.
/// Chunks without matching graph entities pass through with their original score.
/// After blending, the adjusted scores are persisted back to the graph via
/// <see cref="IGraphDatabaseBackend.UpdateNodeWeightAsync"/> so that future
/// retrievals benefit from accumulated feedback.
/// </remarks>
public sealed class FeedbackWeightedScorer : IFeedbackWeightedScorer
{
    private readonly IFeedbackStore _feedbackStore;
    private readonly IGraphDatabaseBackend _graphBackend;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<FeedbackWeightedScorer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FeedbackWeightedScorer"/> class.
    /// </summary>
    /// <param name="feedbackStore">Feedback weight storage.</param>
    /// <param name="graphBackend">Graph database backend for weight persistence.</param>
    /// <param name="configMonitor">Application configuration for alpha value.</param>
    /// <param name="logger">Logger for recording blending decisions.</param>
    public FeedbackWeightedScorer(
        IFeedbackStore feedbackStore,
        IGraphDatabaseBackend graphBackend,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<FeedbackWeightedScorer> logger)
    {
        ArgumentNullException.ThrowIfNull(feedbackStore);
        ArgumentNullException.ThrowIfNull(graphBackend);
        ArgumentNullException.ThrowIfNull(configMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _feedbackStore = feedbackStore;
        _graphBackend = graphBackend;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RerankedResult>> BlendFeedbackAsync(
        IReadOnlyList<RerankedResult> rerankedResults,
        string query,
        CancellationToken cancellationToken = default)
    {
        var alpha = _configMonitor.CurrentValue.AI.Rag.GraphRag.FeedbackAlpha;
        var chunkIds = rerankedResults
            .Select(r => r.RetrievalResult.Chunk.Id)
            .ToList();

        var triplets = await _graphBackend.GetTripletsAsync(chunkIds, cancellationToken);
        if (triplets.Count == 0)
        {
            _logger.LogDebug("No graph triplets found for chunk IDs; skipping feedback blending");
            return rerankedResults;
        }

        var chunkToNodeIds = BuildChunkToNodeMap(triplets);

        var allNodeIds = chunkToNodeIds.Values
            .SelectMany(ids => ids)
            .Distinct()
            .ToList();
        var nodeWeights = await _feedbackStore.GetNodeWeightsBatchAsync(allNodeIds, cancellationToken);

        var adjusted = new List<RerankedResult>(rerankedResults.Count);
        var blendedCount = 0;
        var nodeAdjustedWeights = new Dictionary<string, double>();

        foreach (var result in rerankedResults)
        {
            var chunkId = result.RetrievalResult.Chunk.Id;
            if (!chunkToNodeIds.TryGetValue(chunkId, out var nodeIds) || nodeIds.Count == 0)
            {
                adjusted.Add(result);
                continue;
            }

            var avgWeight = nodeIds
                .Where(nodeWeights.ContainsKey)
                .Select(id => nodeWeights[id].Weight)
                .DefaultIfEmpty(1.0)
                .Average();

            var adjustedScore = (1 - alpha) * result.RerankScore + alpha * avgWeight;
            adjusted.Add(result with { RerankScore = adjustedScore });
            blendedCount++;

            foreach (var nodeId in nodeIds)
            {
                nodeAdjustedWeights[nodeId] = adjustedScore;
            }
        }

        var sorted = adjusted
            .OrderByDescending(r => r.RerankScore)
            .Select((r, i) => r with { RerankRank = i + 1 })
            .ToList();

        // Persist adjusted weights back to graph backend
        foreach (var (nodeId, adjustedWeight) in nodeAdjustedWeights)
        {
            var clampedWeight = Math.Clamp(adjustedWeight, 0.0, 1.0);
            await _graphBackend.UpdateNodeWeightAsync(nodeId, clampedWeight, cancellationToken);
        }

        _logger.LogInformation(
            "Feedback blending: {Blended}/{Total} results adjusted, alpha={Alpha:F2}, {PersistCount} weights persisted",
            blendedCount, rerankedResults.Count, alpha, nodeAdjustedWeights.Count);

        return sorted;
    }

    private static Dictionary<string, List<string>> BuildChunkToNodeMap(
        IReadOnlyList<Domain.AI.KnowledgeGraph.Models.GraphTriplet> triplets)
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var t in triplets)
        {
            foreach (var chunkId in t.Source.ChunkIds)
            {
                if (!map.TryGetValue(chunkId, out var list))
                    map[chunkId] = list = [];
                list.Add(t.Source.Id);
            }

            foreach (var chunkId in t.Target.ChunkIds)
            {
                if (!map.TryGetValue(chunkId, out var list))
                    map[chunkId] = list = [];
                list.Add(t.Target.Id);
            }
        }

        return map;
    }
}
