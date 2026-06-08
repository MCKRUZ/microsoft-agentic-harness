using Domain.AI.Changes;

namespace Domain.AI.Governance;

/// <summary>
/// Carries the outcome of a graded-autonomy evaluation along with the inputs that
/// produced it. The orchestrator records the result on the proposal's audit history
/// so a reviewer can reconstruct why a given proposal was (or was not) routed
/// through the approval gate.
/// </summary>
/// <param name="Decision">The chosen action.</param>
/// <param name="Tier">The agent's effective autonomy tier at evaluation time.</param>
/// <param name="BlastRadius">The proposal's submitted blast radius.</param>
/// <param name="TargetKind">The proposal's target kind.</param>
/// <param name="IsStateChange">True when the proposal mutates state (writes a file, applies a deployment, runs a migration).</param>
/// <param name="Environment">The host environment name (Development / Staging / Production / etc.) at evaluation time.</param>
/// <param name="SkillKey">The skill key that produced the proposal, or null when not skill-attributable.</param>
/// <param name="Reason">A human-readable explanation pinning the rule that drove the decision. Surfaces in audit lines.</param>
public sealed record AutonomyDecisionResult(
    AutonomyDecision Decision,
    AutonomyLevel Tier,
    BlastRadius BlastRadius,
    ChangeTargetKind TargetKind,
    bool IsStateChange,
    string Environment,
    string? SkillKey,
    string Reason);
