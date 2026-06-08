using Domain.AI.Changes;
using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Evaluates the graded-autonomy decision for a single proposal context (PR-4).
/// Layers the configured per-environment and per-skill overrides on top of the
/// tier defaults registered in <c>PermissionsConfig.TierPolicies</c> and returns
/// an <see cref="AutonomyDecisionResult"/> the gate resolver consumes.
/// </summary>
/// <remarks>
/// <para>
/// The evaluator is the single source of truth for "is this proposal auto-approvable
/// under the current tier, environment, and skill?". The gate resolver consults it
/// when deciding whether to include the approval gate in a proposal's frozen gate
/// list. The orchestrator's audit also records the result for forensic reconstruction.
/// </para>
/// <para>
/// The decision is read-mostly — callers do not block on it, and a single evaluation
/// is cheap (dictionary lookups + a few enum comparisons). The result is not cached;
/// configuration hot-reload should propagate immediately.
/// </para>
/// <para>
/// When <see cref="Domain.Common.Config.AI.Permissions.GradedAutonomyConfig.Enabled"/>
/// is false the evaluator falls back to the PR-2 behavior: every non-<see cref="BlastRadius.Trivial"/>
/// proposal returns <see cref="AutonomyDecision.RequiresApproval"/>.
/// </para>
/// </remarks>
public interface IAutonomyDecisionEvaluator
{
    /// <summary>
    /// Compute the graded-autonomy decision for the given proposal context.
    /// </summary>
    /// <param name="tier">The agent's effective autonomy tier (resolved by <see cref="IAutonomyTierResolver"/>).</param>
    /// <param name="radius">The proposal's submitted blast radius.</param>
    /// <param name="targetKind">The proposal's target kind. Reserved for future per-target rules.</param>
    /// <param name="isStateChange">True when the proposal mutates state. Drives the safety-default lock.</param>
    /// <param name="skillKey">The skill key that produced the proposal, or null when not skill-attributable.</param>
    /// <returns>The decision result with the rule that produced it.</returns>
    AutonomyDecisionResult Evaluate(
        AutonomyLevel tier,
        BlastRadius radius,
        ChangeTargetKind targetKind,
        bool isStateChange,
        string? skillKey);
}
