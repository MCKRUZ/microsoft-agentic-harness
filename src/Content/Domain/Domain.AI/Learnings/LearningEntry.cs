using Domain.AI.KnowledgeGraph.Models;

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
    /// Write-time trust classification, set by the memory write gate in
    /// <c>RememberCommandHandler</c>. Only <see cref="MemoryTrust.Trusted"/> learnings are returned
    /// by recall; an <see cref="MemoryTrust.Untrusted"/> learning is retained for audit but never
    /// replayed into an agent's instructions (see <c>LearningEntryTrustExtensions.IsRecallable</c>).
    /// </summary>
    /// <remarks>
    /// Deliberately the <em>same</em> <see cref="MemoryTrust"/> vocabulary the knowledge-memory
    /// channel uses rather than a learnings-specific enum. Both channels persist model- or
    /// conversation-derived text and replay it into the instruction channel later, so they face the
    /// identical risk and are gated by the identical <c>IMemoryWriteGate</c>; two enums would be two
    /// ladders to keep in sync, and the one that drifted would be the one nobody was watching.
    /// Defaults to <see cref="MemoryTrust.Trusted"/> so entries written before this field existed,
    /// and entries written while the guard is disabled, stay recallable.
    /// </remarks>
    public MemoryTrust Trust { get; init; } = MemoryTrust.Trusted;

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
