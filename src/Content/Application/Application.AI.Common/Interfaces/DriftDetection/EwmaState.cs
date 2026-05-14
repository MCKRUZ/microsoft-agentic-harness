using Domain.AI.DriftDetection;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Persisted EWMA running state for a single scope+dimension combination.
/// Stored as a knowledge graph node using <see cref="DeterministicId"/> for O(1) lookup.
/// </summary>
public sealed record EwmaState
{
    /// <summary>The hierarchy level of this EWMA state.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>Identifies the entity within the scope.</summary>
    public required string ScopeIdentifier { get; init; }

    /// <summary>The quality dimension this state tracks.</summary>
    public required DriftDimension Dimension { get; init; }

    /// <summary>The current EWMA-smoothed value.</summary>
    public required double CurrentEwma { get; init; }

    /// <summary>Number of samples incorporated into this EWMA.</summary>
    public required int SampleCount { get; init; }

    /// <summary>When this state was last updated.</summary>
    public required DateTimeOffset LastUpdatedAt { get; init; }

    /// <summary>
    /// Deterministic ID for graph node storage: "ewma:{Scope}:{ScopeIdentifier}:{Dimension}".
    /// </summary>
    public string DeterministicId => $"ewma:{Scope}:{ScopeIdentifier}:{Dimension}";
}
