namespace Domain.AI.DriftDetection;

/// <summary>
/// The drift measurement result for a single evaluation, comparing per-dimension
/// scores against a <see cref="DriftBaseline"/>. Produced by <c>IDriftDetectionService</c>.
/// </summary>
public sealed record DriftScore
{
    /// <summary>Unique identifier for this score.</summary>
    public required Guid ScoreId { get; init; }

    /// <summary>The baseline this score was compared against.</summary>
    public required Guid BaselineId { get; init; }

    /// <summary>The scope of the evaluation.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>Identifies the entity within the scope.</summary>
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension comparison results.</summary>
    public required IReadOnlyDictionary<DriftDimension, DriftDimensionScore> Dimensions { get; init; }

    /// <summary>
    /// Maximum deviation across all dimensions (in sigma units).
    /// This single value drives the severity classification.
    /// </summary>
    public required double OverallDrift { get; init; }

    /// <summary>Classified severity based on threshold configuration.</summary>
    public required DriftSeverity Severity { get; init; }

    /// <summary>When this score was computed.</summary>
    public required DateTimeOffset ScoredAt { get; init; }
}
