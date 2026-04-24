using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Feedback;

/// <summary>
/// In-memory <see cref="IFeedbackStore"/> implementation using exponential moving
/// average (EMA) for weight updates. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// EMA formula: <c>newWeight = alpha * normalizedScore + (1 - alpha) * oldWeight</c>.
/// Feedback scores (1–5) are normalized to 0.0–1.0 before blending.
/// Default weight for entities without feedback is 1.0 (neutral).
/// </para>
/// <para>
/// For production persistence, this store's state should be backed by the same
/// database as the knowledge graph (PostgreSQL JSONB or a dedicated feedback table).
/// The in-memory implementation is suitable for single-process deployments.
/// </para>
/// </remarks>
public sealed class GraphFeedbackStore : IFeedbackStore
{
    private readonly ConcurrentDictionary<string, NodeFeedbackWeight> _nodeWeights = new();
    private readonly ConcurrentDictionary<string, EdgeFeedbackWeight> _edgeWeights = new();
    private readonly ILogger<GraphFeedbackStore> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphFeedbackStore"/> class.
    /// </summary>
    /// <param name="logger">Logger for recording feedback operations.</param>
    /// <param name="timeProvider">Time provider for timestamps.</param>
    public GraphFeedbackStore(
        ILogger<GraphFeedbackStore> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<NodeFeedbackWeight> GetNodeWeightAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        var weight = _nodeWeights.GetValueOrDefault(nodeId, DefaultNodeWeight(nodeId));
        return Task.FromResult(weight);
    }

    /// <inheritdoc />
    public Task<EdgeFeedbackWeight> GetEdgeWeightAsync(
        string edgeId,
        CancellationToken cancellationToken = default)
    {
        var weight = _edgeWeights.GetValueOrDefault(edgeId, DefaultEdgeWeight(edgeId));
        return Task.FromResult(weight);
    }

    /// <inheritdoc />
    public Task ApplyNodeFeedbackAsync(
        string nodeId,
        double feedbackScore,
        double alpha,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeScore(feedbackScore);
        _nodeWeights.AddOrUpdate(
            nodeId,
            _ => new NodeFeedbackWeight
            {
                NodeId = nodeId,
                Weight = normalized,
                UpdateCount = 1,
                LastUpdatedAt = _timeProvider.GetUtcNow()
            },
            (_, existing) => existing with
            {
                Weight = alpha * normalized + (1 - alpha) * existing.Weight,
                UpdateCount = existing.UpdateCount + 1,
                LastUpdatedAt = _timeProvider.GetUtcNow()
            });

        _logger.LogDebug(
            "Applied node feedback: NodeId={NodeId}, Score={Score}, Alpha={Alpha}",
            nodeId, feedbackScore, alpha);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ApplyEdgeFeedbackAsync(
        string edgeId,
        double feedbackScore,
        double alpha,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeScore(feedbackScore);
        _edgeWeights.AddOrUpdate(
            edgeId,
            _ => new EdgeFeedbackWeight
            {
                EdgeId = edgeId,
                Weight = normalized,
                UpdateCount = 1,
                LastUpdatedAt = _timeProvider.GetUtcNow()
            },
            (_, existing) => existing with
            {
                Weight = alpha * normalized + (1 - alpha) * existing.Weight,
                UpdateCount = existing.UpdateCount + 1,
                LastUpdatedAt = _timeProvider.GetUtcNow()
            });

        _logger.LogDebug(
            "Applied edge feedback: EdgeId={EdgeId}, Score={Score}, Alpha={Alpha}",
            edgeId, feedbackScore, alpha);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, NodeFeedbackWeight>> GetNodeWeightsBatchAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, NodeFeedbackWeight>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
            result[nodeId] = _nodeWeights.GetValueOrDefault(nodeId, DefaultNodeWeight(nodeId));

        return Task.FromResult<IReadOnlyDictionary<string, NodeFeedbackWeight>>(result);
    }

    private static double NormalizeScore(double score) =>
        Math.Clamp((score - 1.0) / 4.0, 0.0, 1.0);

    private static NodeFeedbackWeight DefaultNodeWeight(string nodeId) => new()
    {
        NodeId = nodeId, Weight = 1.0, UpdateCount = 0,
        LastUpdatedAt = DateTimeOffset.MinValue
    };

    private static EdgeFeedbackWeight DefaultEdgeWeight(string edgeId) => new()
    {
        EdgeId = edgeId, Weight = 1.0, UpdateCount = 0,
        LastUpdatedAt = DateTimeOffset.MinValue
    };
}
