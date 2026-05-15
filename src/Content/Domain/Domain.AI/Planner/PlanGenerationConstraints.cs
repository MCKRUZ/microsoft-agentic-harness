namespace Domain.AI.Planner;

/// <summary>
/// Optional constraints applied during LLM-driven plan generation to bound
/// the complexity and resource usage of generated plans.
/// </summary>
public sealed record PlanGenerationConstraints
{
    /// <summary>Maximum number of steps the generated plan may contain.</summary>
    public int? MaxSteps { get; init; }

    /// <summary>Step types the generator is allowed to use.</summary>
    public IReadOnlyList<StepType>? AllowedStepTypes { get; init; }

    /// <summary>Maximum depth of nested sub-plan invocations.</summary>
    public int? MaxSubPlanDepth { get; init; }

    /// <summary>Maximum total execution timeout for the generated plan.</summary>
    public TimeSpan? MaxTotalTimeout { get; init; }

    /// <summary>Additional context or instructions to include in the generation prompt.</summary>
    public string? AdditionalContext { get; init; }
}
