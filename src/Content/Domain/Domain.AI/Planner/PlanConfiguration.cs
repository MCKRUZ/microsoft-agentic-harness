namespace Domain.AI.Planner;

/// <summary>
/// Plan-level execution settings controlling timeouts, parallelism, and recursion depth.
/// </summary>
public sealed record PlanConfiguration
{
    /// <summary>Maximum wall-clock time for the entire plan before forced termination.</summary>
    public TimeSpan PlanTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Maximum number of steps that may execute concurrently.</summary>
    public int MaxParallelSteps { get; init; } = 10;

    /// <summary>
    /// Maximum depth of nested sub-plan invocations. Prevents unbounded recursion
    /// when plans invoke child plans that themselves invoke further child plans.
    /// </summary>
    public int MaxSubPlanDepth { get; init; } = 5;
}
