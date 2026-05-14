using Domain.AI.DriftDetection;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Query DTO for retrieving historical drift scores within a time window.
/// </summary>
public sealed record DriftHistoryQuery
{
    /// <summary>The hierarchy level to query.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>Identifies the entity within the scope.</summary>
    public required string ScopeIdentifier { get; init; }

    /// <summary>Start of the query window (inclusive).</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>End of the query window (inclusive).</summary>
    public required DateTimeOffset End { get; init; }
}
