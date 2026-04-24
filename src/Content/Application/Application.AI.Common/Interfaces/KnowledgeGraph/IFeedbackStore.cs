using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Stores and retrieves feedback weights for knowledge graph nodes and edges.
/// Weights track historical retrieval quality and are used to re-rank future results
/// by blending semantic relevance with accumulated feedback signals.
/// </summary>
/// <remarks>
/// Weights use exponential moving average (EMA):
/// <c>newWeight = alpha * feedbackScore + (1 - alpha) * oldWeight</c>,
/// where <c>alpha</c> is configured via <c>GraphRagConfig.FeedbackAlpha</c>.
/// </remarks>
public interface IFeedbackStore
{
    /// <summary>
    /// Gets the feedback weight for a node. Returns a default weight of 1.0
    /// if no feedback has been recorded for the node.
    /// </summary>
    /// <param name="nodeId">The node identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<NodeFeedbackWeight> GetNodeWeightAsync(
        string nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the feedback weight for an edge. Returns a default weight of 1.0
    /// if no feedback has been recorded for the edge.
    /// </summary>
    /// <param name="edgeId">The edge identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EdgeFeedbackWeight> GetEdgeWeightAsync(
        string edgeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a feedback score to a node, updating its weight via EMA.
    /// </summary>
    /// <param name="nodeId">The node identifier.</param>
    /// <param name="feedbackScore">The feedback score (1.0–5.0).</param>
    /// <param name="alpha">The EMA learning rate (0.0–1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyNodeFeedbackAsync(
        string nodeId,
        double feedbackScore,
        double alpha,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a feedback score to an edge, updating its weight via EMA.
    /// </summary>
    /// <param name="edgeId">The edge identifier.</param>
    /// <param name="feedbackScore">The feedback score (1.0–5.0).</param>
    /// <param name="alpha">The EMA learning rate (0.0–1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyEdgeFeedbackAsync(
        string edgeId,
        double feedbackScore,
        double alpha,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets feedback weights for multiple nodes in a single call.
    /// Returns default weights for nodes without feedback history.
    /// </summary>
    /// <param name="nodeIds">The node identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, NodeFeedbackWeight>> GetNodeWeightsBatchAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default);
}
