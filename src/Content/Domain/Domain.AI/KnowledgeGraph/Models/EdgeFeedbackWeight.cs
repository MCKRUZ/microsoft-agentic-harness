namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Tracks accumulated feedback weight for a <see cref="GraphEdge"/>, reflecting how
/// useful this relationship has been in past retrieval results. Edge weights complement
/// <see cref="NodeFeedbackWeight"/> by capturing the quality of specific relationships
/// rather than individual entities.
/// </summary>
/// <remarks>
/// Edge weights are particularly valuable for disambiguation: when a node participates
/// in many relationships, edge weights help the retriever prefer the relationships
/// that have historically produced useful context over noisy or irrelevant ones.
/// Updated via the same exponential moving average as node weights.
/// </remarks>
public record EdgeFeedbackWeight
{
    /// <summary>
    /// The ID of the <see cref="GraphEdge"/> this weight applies to.
    /// </summary>
    public required string EdgeId { get; init; }

    /// <summary>
    /// The accumulated feedback weight, normalized to a 0.0–1.0 range where 0.5 is neutral.
    /// </summary>
    public required double Weight { get; init; }

    /// <summary>
    /// The number of feedback signals that have been applied to this weight.
    /// </summary>
    public required int UpdateCount { get; init; }

    /// <summary>
    /// When this weight was last updated by a feedback signal.
    /// </summary>
    public required DateTimeOffset LastUpdatedAt { get; init; }
}
