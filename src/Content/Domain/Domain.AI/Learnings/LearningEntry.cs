namespace Domain.AI.Learnings;

/// <summary>
/// The core learning record, representing a piece of knowledge captured from corrections,
/// drift events, escalation resolutions, or manual entries. Persisted as a graph node by
/// <c>GraphLearningsStore</c> with deterministic ID <c>"learning:{LearningId}"</c>.
/// </summary>
/// <remarks>
/// <see cref="FeedbackWeight"/> is updated via exponential moving average in
/// <c>ImproveLearningCommandHandler</c>. Higher weights indicate learnings that have been
/// repeatedly validated as useful. The weight influences recall ranking via the formula:
/// <c>finalScore = (1 - alpha) * relevance + alpha * min(feedback * freshness, ceiling)</c>.
/// <see cref="DecayClass"/> determines temporal decay behavior. <see cref="LastReinforcedAt"/>
/// resets the decay clock when a learning receives positive feedback.
/// </remarks>
public sealed record LearningEntry
{
    /// <summary>Unique identifier for this learning.</summary>
    public required Guid LearningId { get; init; }

    /// <summary>What kind of knowledge this learning represents.</summary>
    public required LearningCategory Category { get; init; }

    /// <summary>How quickly this learning decays over time.</summary>
    public required DecayClass DecayClass { get; init; }

    /// <summary>Visibility scope (agent, team, or global).</summary>
    public required LearningScope Scope { get; init; }

    /// <summary>The actual knowledge content -- a natural language description of what was learned.</summary>
    public required string Content { get; init; }

    /// <summary>What produced this learning.</summary>
    public required LearningSource Source { get; init; }

    /// <summary>Pipeline provenance metadata.</summary>
    public required LearningProvenance Provenance { get; init; }

    /// <summary>
    /// EMA-weighted feedback score. Default 1.0 (neutral). Updated by
    /// <c>ImproveLearningCommandHandler</c>. Range: 0.0+ (no upper bound enforced at
    /// domain level; ceiling applied during recall scoring).
    /// </summary>
    public double FeedbackWeight { get; init; } = 1.0;

    /// <summary>Number of times this learning's feedback weight has been updated.</summary>
    public int UpdateCount { get; init; }

    /// <summary>When this learning was first created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this learning was last accessed during a recall query. Null if never recalled.</summary>
    public DateTimeOffset? LastAccessedAt { get; init; }

    /// <summary>
    /// When this learning was last reinforced via positive feedback. Null if never reinforced.
    /// Used by <c>DefaultLearningDecayService</c> to reset the decay clock.
    /// </summary>
    public DateTimeOffset? LastReinforcedAt { get; init; }

    /// <summary>Soft-delete flag. Deleted learnings remain in the graph for audit but are excluded from search.</summary>
    public bool IsDeleted { get; init; }

    /// <summary>Reason for soft-deletion. Null when not deleted.</summary>
    public string? DeleteReason { get; init; }
}
