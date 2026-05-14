using Domain.AI.DriftDetection;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Request DTO for recalculating a drift baseline from recent evaluation history.
/// </summary>
public sealed record DriftBaselineUpdateRequest
{
    /// <summary>The hierarchy level of the baseline to update.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>Identifies the entity within the scope (agent ID, skill name, or task type).</summary>
    public required string ScopeIdentifier { get; init; }
}
