using Domain.AI.Changes;
using Domain.AI.Permissions;

namespace Domain.AI.Governance;

/// <summary>
/// Maps an autonomy level to its baseline permission behavior, its per-tool overrides,
/// and (PR-4) its per-<see cref="BlastRadius"/> decision table for the
/// <c>ChangeProposal</c> pipeline. Pure value object with no behavior beyond
/// <see cref="EvaluateFor"/> — a deterministic projection of its inputs.
/// </summary>
/// <remarks>
/// <para>
/// Two orthogonal jobs live on this record:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Tool permission baseline</b> — <see cref="DefaultBehavior"/> and
///     <see cref="ToolOverrides"/> are consumed by <c>AutonomyTierRuleProvider</c>
///     to emit <c>ToolPermissionRule</c> entries for runtime tool-invocation gating.
///   </description></item>
///   <item><description>
///     <b>Graded autonomy decision</b> — <see cref="PerBlastRadius"/> and
///     <see cref="EvaluateFor"/> are consumed by the gate resolver / autonomy
///     evaluator to decide whether a <see cref="ChangeProposal"/> at a given
///     <see cref="BlastRadius"/> may auto-approve under this tier.
///   </description></item>
/// </list>
/// <para>
/// The two surfaces are kept on the same record because they share an input
/// (the tier) and consumers configuring autonomy think of them together. They do
/// not interact: setting tool overrides does not change blast-radius decisions
/// and vice versa.
/// </para>
/// <para>
/// <b>Naming note.</b> The plan documents the trust tiers as
/// "Manual / Supervised / Autonomous"; the harness's <see cref="AutonomyLevel"/>
/// enum uses <see cref="AutonomyLevel.Restricted"/> in place of "Manual"
/// (numeric ordering and "ask for everything" semantics match). All API surfaces
/// here use the <see cref="AutonomyLevel"/> names.
/// </para>
/// </remarks>
public sealed record AutonomyTierPolicy
{
    /// <summary>Which tier this policy applies to.</summary>
    public required AutonomyLevel Level { get; init; }

    /// <summary>
    /// The baseline permission behavior for this tier.
    /// Restricted and Supervised map to Ask; Autonomous maps to Allow.
    /// </summary>
    public required PermissionBehaviorType DefaultBehavior { get; init; }

    /// <summary>
    /// Per-tool behavior overrides within the tier. For example, a Restricted agent
    /// might still Allow <c>"query_knowledge_graph"</c>. Null means no overrides.
    /// </summary>
    public IReadOnlyDictionary<string, PermissionBehaviorType>? ToolOverrides { get; init; }

    /// <summary>
    /// Per-<see cref="BlastRadius"/> autonomy decision table consumed by the
    /// <c>ChangeProposal</c> pipeline. Null means "no graded rules" — the evaluator
    /// falls back to its environment-wide defaults
    /// (<see cref="AutonomyDecision.RequiresApproval"/> for everything above
    /// <see cref="BlastRadius.Trivial"/>).
    /// </summary>
    /// <remarks>
    /// The dictionary is keyed by <see cref="BlastRadius"/> so the consumer can
    /// declare e.g. <c>Trivial =&gt; AutoApprove</c> and <c>Low =&gt; AutoApprove</c>
    /// without having to enumerate every radius. Radii not present in the map fall
    /// back to <see cref="AutonomyDecision.RequiresApproval"/>.
    /// </remarks>
    public IReadOnlyDictionary<BlastRadius, BlastRadiusAutonomyRule>? PerBlastRadius { get; init; }

    /// <summary>
    /// Evaluate the autonomy decision for a single proposal context against this
    /// tier policy. Pure function: returns the same answer for the same inputs and
    /// never mutates state. Does not consider environment or skill — those are
    /// resolved by <c>IAutonomyDecisionEvaluator</c> before it picks which policy
    /// to evaluate against.
    /// </summary>
    /// <param name="radius">The proposal's blast radius.</param>
    /// <param name="targetKind">The proposal's target kind. Reserved for future per-target rules; currently unused but accepted so callers thread it through ready for PR-9/10.</param>
    /// <param name="isStateChange">True when the proposal mutates state (writes a file, applies a deployment, etc.). The hard invariant: state-changers default to <see cref="AutonomyDecision.RequiresApproval"/> regardless of tier, unless the matching <see cref="BlastRadiusAutonomyRule.AllowAutoApproveForStateChange"/> is true.</param>
    /// <returns>The decision and the rule that produced it.</returns>
    public (AutonomyDecision Decision, string Reason) EvaluateFor(
        BlastRadius radius,
        ChangeTargetKind targetKind,
        bool isStateChange)
    {
        _ = targetKind; // reserved for future per-target rules; consumed by evaluator caller.

        // Critical blast radius is special-cased at the tier level: even an
        // Autonomous tier that lists Critical => AutoApprove must still route
        // through approval. This is the load-bearing safety rule and lives on
        // the policy itself so a misconfigured rule table cannot circumvent it.
        if (radius == BlastRadius.Critical)
        {
            return (
                AutonomyDecision.RequiresApproval,
                "blast radius Critical always requires approval regardless of tier configuration.");
        }

        if (PerBlastRadius is not null && PerBlastRadius.TryGetValue(radius, out var rule))
        {
            // The hard invariant: state-changers default to RequiresApproval.
            if (rule.Decision == AutonomyDecision.AutoApprove
                && isStateChange
                && !rule.AllowAutoApproveForStateChange)
            {
                return (
                    AutonomyDecision.RequiresApproval,
                    $"tier {Level} at blast radius {radius} would auto-approve, but the proposal is a state-change " +
                    "and AllowAutoApproveForStateChange is false (the safety-default).");
            }

            return (
                rule.Decision,
                $"tier {Level} rule for blast radius {radius} → {rule.Decision} " +
                $"(state-change opt-in: {rule.AllowAutoApproveForStateChange}).");
        }

        // No explicit rule: default to requiring approval. Anything that needs to
        // auto-approve must declare it explicitly. The state-change default is
        // separately enforced because Trivial-non-state-change is the only case
        // where this default could plausibly be loosened to AutoApprove by a
        // consumer — and they have to do that explicitly via a rule.
        return (
            AutonomyDecision.RequiresApproval,
            $"tier {Level} has no rule for blast radius {radius} — defaulting to RequiresApproval.");
    }
}
