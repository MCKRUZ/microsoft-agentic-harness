namespace Domain.AI.Planner;

/// <summary>
/// Summary of a completed or failed plan execution, returned by the plan executor.
/// </summary>
public sealed record PlanExecutionSummary
{
    /// <summary>The plan that was executed.</summary>
    public required PlanId PlanId { get; init; }

    /// <summary>Final execution status of the overall plan.</summary>
    public required StepExecutionStatus FinalStatus { get; init; }

    /// <summary>Total wall-clock duration of the execution.</summary>
    public required TimeSpan TotalDuration { get; init; }

    /// <summary>Per-step execution states at completion.</summary>
    public required IReadOnlyList<StepExecutionState> StepStates { get; init; }

    /// <summary>Number of steps that completed successfully.</summary>
    public int CompletedStepCount { get; init; }

    /// <summary>Number of steps that failed.</summary>
    public int FailedStepCount { get; init; }

    /// <summary>Number of steps that were skipped.</summary>
    public int SkippedStepCount { get; init; }
}
