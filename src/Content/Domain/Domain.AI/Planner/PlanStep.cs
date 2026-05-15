using Domain.AI.Governance;

namespace Domain.AI.Planner;

/// <summary>
/// A node in a <see cref="PlanGraph"/> representing a single executable unit of work.
/// Each step has a type determining which executor handles it, configuration specific
/// to that type, and retry/timeout policies.
/// </summary>
public sealed record PlanStep
{
    /// <summary>Unique identifier for this step within the plan.</summary>
    public required PlanStepId Id { get; init; }

    /// <summary>Human-readable name describing the step's purpose.</summary>
    public required string Name { get; init; }

    /// <summary>Determines which keyed executor handles this step.</summary>
    public required StepType Type { get; init; }

    /// <summary>Type-specific configuration for the step executor.</summary>
    public required StepConfiguration Configuration { get; init; }

    /// <summary>Retry behavior when the step fails.</summary>
    public required RetryPolicy RetryPolicy { get; init; }

    /// <summary>Maximum wall-clock time for this individual step.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Minimum autonomy level required to execute this step. When null, the
    /// agent's current autonomy level is used without additional checks.
    /// </summary>
    public AutonomyLevel? RequiredAutonomyLevel { get; init; }
}
