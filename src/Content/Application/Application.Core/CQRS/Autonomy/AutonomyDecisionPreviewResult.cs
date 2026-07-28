using Domain.AI.Agents;
using Domain.AI.Changes;
using Domain.AI.Governance;

namespace Application.Core.CQRS.Autonomy;

/// <summary>
/// The outcome of a side-effect-free autonomy decision preview: what the graded-autonomy
/// evaluator would decide for the described action, plus the inputs the decision was computed
/// from. Mirrors <see cref="AutonomyDecisionResult"/> with the subagent type the caller asked
/// about added for self-describing responses.
/// </summary>
/// <param name="SubagentType">The subagent type the preview was computed for.</param>
/// <param name="Decision">The evaluator's verdict: <c>AutoApprove</c> (allowed without a gate), <c>RequiresApproval</c> (routes through human escalation), or <c>Forbidden</c> (denied outright).</param>
/// <param name="Tier">The subagent's effective autonomy tier at evaluation time.</param>
/// <param name="BlastRadius">The proposed action's blast radius, as submitted.</param>
/// <param name="TargetKind">The proposed action's target kind, as submitted.</param>
/// <param name="IsStateChange">Whether the proposed action mutates state.</param>
/// <param name="Environment">The host environment name the decision was evaluated under.</param>
/// <param name="SkillKey">The skill key the decision was evaluated for, or null when not skill-attributable.</param>
/// <param name="Reason">Human-readable explanation pinning the rule that drove the decision.</param>
public sealed record AutonomyDecisionPreviewResult(
    SubagentType SubagentType,
    AutonomyDecision Decision,
    AutonomyLevel Tier,
    BlastRadius BlastRadius,
    ChangeTargetKind TargetKind,
    bool IsStateChange,
    string Environment,
    string? SkillKey,
    string Reason)
{
    /// <summary>
    /// Projects the evaluator's <see cref="AutonomyDecisionResult"/> into the preview shape.
    /// </summary>
    /// <param name="subagentType">The subagent type the preview was computed for.</param>
    /// <param name="result">The evaluator's decision result.</param>
    /// <returns>The preview projection.</returns>
    public static AutonomyDecisionPreviewResult FromResult(
        SubagentType subagentType, AutonomyDecisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AutonomyDecisionPreviewResult(
            subagentType,
            result.Decision,
            result.Tier,
            result.BlastRadius,
            result.TargetKind,
            result.IsStateChange,
            result.Environment,
            result.SkillKey,
            result.Reason);
    }
}
