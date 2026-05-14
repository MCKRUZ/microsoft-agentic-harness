namespace Domain.AI.Learnings;

/// <summary>
/// A learning entry enriched with computed relevance, feedback, and freshness scores.
/// Returned by <c>RecallQueryHandler</c> after the full scoring pipeline:
/// <c>FinalScore = (1 - alpha) * RelevanceScore + alpha * min(FeedbackScore * FreshnessScore, ceiling)</c>.
/// </summary>
/// <remarks>
/// All score fields are pre-computed by the handler. The record is a pure data carrier --
/// it does not calculate <see cref="FinalScore"/> from the component scores.
/// </remarks>
public sealed record WeightedLearning
{
    /// <summary>The underlying learning entry.</summary>
    public required LearningEntry Learning { get; init; }

    /// <summary>Semantic similarity between the recall query and the learning content (0.0-1.0).</summary>
    public required double RelevanceScore { get; init; }

    /// <summary>The learning's EMA-weighted feedback score (from <see cref="LearningEntry.FeedbackWeight"/>).</summary>
    public required double FeedbackScore { get; init; }

    /// <summary>Temporal freshness based on decay class and age (0.0-1.0).</summary>
    public required double FreshnessScore { get; init; }

    /// <summary>
    /// The blended final score used for ranking. Pre-computed by the recall handler.
    /// </summary>
    public required double FinalScore { get; init; }
}
