namespace Domain.AI.DriftDetection;

/// <summary>
/// A "known good" quality snapshot for a scope. Drift scores are compared against
/// the baseline's per-dimension means and standard deviations to determine whether
/// quality has degraded.
/// </summary>
/// <remarks>
/// Baselines are stored as knowledge graph nodes with deterministic IDs
/// (<c>"driftbaseline:{scope}:{identifier}"</c>) for O(1) lookup.
/// A new baseline overwrites the previous one; history is tracked via
/// <see cref="DriftAuditRecord"/> entries with <see cref="DriftAuditRecordType.BaselineUpdated"/>.
/// </remarks>
public sealed record DriftBaseline
{
    /// <summary>Unique identifier for this baseline snapshot.</summary>
    public required Guid BaselineId { get; init; }

    /// <summary>The hierarchy level of this baseline.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>
    /// Identifies the entity within the scope (agent ID, skill name, or task type name).
    /// </summary>
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension mean scores from the baseline window.</summary>
    public required IReadOnlyDictionary<DriftDimension, double> Dimensions { get; init; }

    /// <summary>Per-dimension standard deviations from the baseline window.</summary>
    public required IReadOnlyDictionary<DriftDimension, double> DimensionSigmas { get; init; }

    /// <summary>Number of evaluations used to compute this baseline.</summary>
    public required int SampleCount { get; init; }

    /// <summary>Start of the rolling window used for this baseline.</summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>End of the rolling window used for this baseline.</summary>
    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>When this baseline was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
