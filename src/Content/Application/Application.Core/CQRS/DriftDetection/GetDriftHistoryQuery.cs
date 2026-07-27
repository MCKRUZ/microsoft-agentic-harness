using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Retrieves the persisted drift scores for one scope within a bounded time window — every
/// evaluation (healthy and drifted) the subsystem recorded, in the shape baseline
/// recalculation consumes.
/// </summary>
public sealed record GetDriftHistoryQuery : IRequest<Result<IReadOnlyList<DriftScore>>>
{
    /// <summary>The hierarchy level to query.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>Identifies the entity within the scope (agent ID, skill name, or task type).</summary>
    public required string ScopeIdentifier { get; init; }

    /// <summary>Start of the query window (inclusive).</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>End of the query window (inclusive). The window is capped at
    /// <see cref="DriftValidationRules.MaxHistoryWindowDays"/> days.</summary>
    public required DateTimeOffset End { get; init; }
}
