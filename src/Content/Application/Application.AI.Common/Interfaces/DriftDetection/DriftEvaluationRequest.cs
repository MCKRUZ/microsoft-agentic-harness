using Domain.AI.DriftDetection;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Request DTO for evaluating drift against a baseline.
/// Contains the current dimension scores to compare.
/// </summary>
public sealed record DriftEvaluationRequest
{
    /// <summary>The hierarchy level of the evaluation.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>Identifies the entity within the scope (agent ID, skill name, or task type).</summary>
    public required string ScopeIdentifier { get; init; }

    /// <summary>Current dimension scores to evaluate against the baseline.</summary>
    public required IReadOnlyDictionary<DriftDimension, double> Dimensions { get; init; }
}
