namespace Domain.AI.Planner;

/// <summary>
/// Configuration for a conditional branching step. Evaluates a condition expression
/// and directs execution flow to the true or false target step.
/// </summary>
public sealed record ConditionalBranchConfig : StepConfiguration
{
    /// <summary>
    /// Expression evaluated at runtime using the <c>DecisionRule</c> pattern.
    /// Supports JSON path comparisons, boolean operators, and null checks.
    /// </summary>
    public required string ConditionExpression { get; init; }

    /// <summary>Step to activate when the condition evaluates to true.</summary>
    public required PlanStepId TrueEdgeTargetId { get; init; }

    /// <summary>Step to activate when the condition evaluates to false.</summary>
    public required PlanStepId FalseEdgeTargetId { get; init; }
}
