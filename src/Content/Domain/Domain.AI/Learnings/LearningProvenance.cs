namespace Domain.AI.Learnings;

/// <summary>
/// Detailed provenance metadata for a learning entry, tracking which pipeline and task
/// produced the knowledge and with what confidence.
/// </summary>
public sealed record LearningProvenance
{
    /// <summary>The pipeline that produced this learning (e.g., "escalation_resolution", "drift_correction").</summary>
    public required string OriginPipeline { get; init; }

    /// <summary>The specific task within the pipeline (e.g., "human_review", "auto_correct").</summary>
    public required string OriginTask { get; init; }

    /// <summary>When the originating event occurred.</summary>
    public required DateTimeOffset OriginTimestamp { get; init; }

    /// <summary>
    /// Confidence in the learning's correctness, normalized to 0.0-1.0.
    /// Validated by <c>RememberCommandValidator</c> to enforce the [0, 1] range.
    /// </summary>
    public required double Confidence { get; init; }
}
