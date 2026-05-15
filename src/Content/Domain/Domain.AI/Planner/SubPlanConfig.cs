namespace Domain.AI.Planner;

/// <summary>
/// Configuration for a sub-plan invocation step. Executes a child plan in an isolated
/// scope with depth limiting. Exactly one of <see cref="ChildPlanId"/> or
/// <see cref="InlinePlanDefinition"/> must be set (validated in section-03).
/// </summary>
public sealed record SubPlanConfig : StepConfiguration
{
    /// <summary>Reference to an existing persisted plan. Null when using inline definition.</summary>
    public PlanId? ChildPlanId { get; init; }

    /// <summary>Inline plan definition. Null when referencing an existing plan.</summary>
    public PlanGraph? InlinePlanDefinition { get; init; }

    /// <summary>Whether the child plan executes in an isolated context separate from the parent.</summary>
    public bool IsolateContext { get; init; } = true;
}
