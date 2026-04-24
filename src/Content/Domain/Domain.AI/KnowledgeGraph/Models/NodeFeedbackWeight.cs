namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Tracks accumulated feedback weight for a <see cref="GraphNode"/>, reflecting how
/// useful this entity has been in past retrieval results. Higher weights indicate
/// entities that consistently appear in positively-rated responses; lower weights
/// indicate entities associated with unhelpful or corrected answers.
/// </summary>
/// <remarks>
/// Feedback weights are updated via exponential moving average using the configured
/// <c>FeedbackAlpha</c> learning rate: <c>newWeight = alpha * feedbackScore + (1 - alpha) * oldWeight</c>.
/// The <see cref="UpdateCount"/> tracks how many feedback signals have been applied,
/// enabling confidence-based filtering (nodes with few updates have unreliable weights).
/// </remarks>
public record NodeFeedbackWeight
{
    /// <summary>
    /// The ID of the <see cref="GraphNode"/> this weight applies to.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// The accumulated feedback weight, normalized to a 0.0–1.0 range where 0.5 is neutral.
    /// Values above 0.5 indicate positive feedback history; below 0.5 indicates negative.
    /// </summary>
    public required double Weight { get; init; }

    /// <summary>
    /// The number of feedback signals that have been applied to this weight.
    /// Low counts indicate unreliable weights that should be used cautiously.
    /// </summary>
    public required int UpdateCount { get; init; }

    /// <summary>
    /// When this weight was last updated by a feedback signal.
    /// </summary>
    public required DateTimeOffset LastUpdatedAt { get; init; }
}
