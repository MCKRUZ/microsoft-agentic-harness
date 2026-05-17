namespace Domain.AI.Planner;

/// <summary>
/// Immutable context tracking the current plan execution depth and parent plan relationship.
/// Used by <c>SubPlanStepExecutor</c> to enforce maximum recursion depth.
/// Registered as scoped in DI; child scopes receive a new instance with incremented depth.
/// </summary>
public sealed record PlanExecutionContext
{
    /// <summary>Current sub-plan nesting depth. Root plan starts at 0.</summary>
    public int Depth { get; init; }

    /// <summary>The plan ID currently being executed in this scope.</summary>
    public PlanId? CurrentPlanId { get; init; }

    /// <summary>Maximum allowed sub-plan depth before rejecting further nesting.</summary>
    public int MaxDepth { get; init; } = 5;
}
