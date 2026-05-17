using Domain.AI.Planner;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Read model combining a plan graph with its current per-step execution states.
/// Returned by <see cref="GetPlanQuery"/>.
/// </summary>
public sealed record PlanSnapshot
{
    /// <summary>The plan graph structure.</summary>
    public required PlanGraph Graph { get; init; }

    /// <summary>Current execution state per step.</summary>
    public required IReadOnlyDictionary<PlanStepId, StepExecutionState> StepStates { get; init; }
}
