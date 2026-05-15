namespace Domain.AI.Planner;

/// <summary>
/// Outcome of validating a <see cref="PlanGraph"/> before execution.
/// Contains errors that block execution, warnings for informational issues,
/// and an estimated critical path duration.
/// </summary>
public sealed record PlanValidationResult
{
    /// <summary>Whether the plan passed all validation checks.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Validation errors that prevent execution.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Validation warnings that do not prevent execution.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Estimated critical path duration based on step timeouts and graph structure.
    /// Null if validation failed before estimation could complete.
    /// </summary>
    public TimeSpan? EstimatedCriticalPathDuration { get; init; }
}
