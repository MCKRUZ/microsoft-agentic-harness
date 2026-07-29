using Domain.AI.Planner;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// A directed edge between two steps of a submitted <see cref="WorkflowDefinition"/>, referring to
/// them by <see cref="WorkflowStep.Name"/>.
/// </summary>
/// <remarks>
/// <para>
/// Edges carry the workflow's control flow, including the branch targets of a conditional step. A
/// <see cref="ConditionalBranchStepConfiguration"/> declares only its condition; the two outgoing
/// edges labelled <see cref="EdgeType.ConditionalTrue"/> and <see cref="EdgeType.ConditionalFalse"/>
/// say where each outcome goes. This is why the wire contract omits the domain type's
/// <c>TrueEdgeTargetId</c>/<c>FalseEdgeTargetId</c>: with both present a submission can state two
/// different answers to the same question, and something has to decide which one wins.
/// </para>
/// </remarks>
public sealed record WorkflowEdge
{
    /// <summary>
    /// Name of the step this edge leaves. Must match a <see cref="WorkflowStep.Name"/> in the same
    /// submission.
    /// </summary>
    public required string From { get; init; }

    /// <summary>
    /// Name of the step this edge enters. Must match a <see cref="WorkflowStep.Name"/> in the same
    /// submission.
    /// </summary>
    public required string To { get; init; }

    /// <summary>
    /// What this edge means: data flow, plain control flow, or the true/false arm of a conditional
    /// branch. A step of type <c>ConditionalBranch</c> requires exactly one
    /// <see cref="EdgeType.ConditionalTrue"/> and one <see cref="EdgeType.ConditionalFalse"/> outgoing
    /// edge; <c>PlanValidator</c> enforces branch completeness once the plan is built.
    /// </summary>
    public required EdgeType Type { get; init; }

    /// <summary>
    /// Optional guard expression carried on the edge itself. Distinct from a conditional step's own
    /// condition, which selects between two labelled arms.
    /// </summary>
    public string? Condition { get; init; }
}
