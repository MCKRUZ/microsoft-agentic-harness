using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.AI.Governance;

namespace Infrastructure.AI.Changes;

/// <summary>
/// Default <see cref="IChangeProposalGateResolver"/>: maps every supported
/// target kind to the standard four-gate pipeline. Critical blast radius is
/// guaranteed to include the approval gate even if the target's default would
/// otherwise omit it — the resolver is the right place to enforce
/// "Critical always needs approval" because the resulting list is frozen into
/// the proposal at submission time and cannot be lowered later.
/// </summary>
/// <remarks>
/// <para>
/// Consumers needing stricter pipelines (an extra <c>compliance</c> gate for
/// regulated environments, a <c>cost</c> gate for IaC at high blast radius)
/// register their own <see cref="IChangeProposalGateResolver"/> implementation
/// in place of this one.
/// </para>
/// <para>
/// PR-4 wiring: <see cref="ResolveWithDecision"/> consults the supplied
/// <see cref="AutonomyDecisionResult"/> to decide whether the approval gate is
/// included. A null decision (the pre-PR-4 path) falls back to the static
/// "Trivial auto-approves, everything else requires approval" rule. A non-null
/// decision overrides that rule entirely:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="AutonomyDecision.AutoApprove"/> — approval gate omitted.</description></item>
///   <item><description><see cref="AutonomyDecision.RequiresApproval"/> — approval gate included.</description></item>
///   <item><description><see cref="AutonomyDecision.Forbidden"/> — approval gate included; the orchestrator's first-policy-gate rejection turns the proposal into Rejected without invoking the approval router.</description></item>
/// </list>
/// </remarks>
public sealed class DefaultChangeProposalGateResolver : IChangeProposalGateResolver
{
    private static readonly IReadOnlyList<string> StandardPipeline =
    [
        WellKnownGateKeys.SelfValidation,
        WellKnownGateKeys.Policy,
        WellKnownGateKeys.Approval,
        WellKnownGateKeys.Merge
    ];

    private static readonly IReadOnlyList<string> AutoApprovePipeline =
    [
        WellKnownGateKeys.SelfValidation,
        WellKnownGateKeys.Policy,
        WellKnownGateKeys.Merge
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Resolve(ChangeTargetKind targetKind, BlastRadius blastRadius)
    {
        // Trivial blast radius (cosmetic / comment-only) may auto-approve.
        // Anything Medium or higher always passes through approval.
        // Low is a judgment call — default to requiring approval; consumers
        // who want auto-approve at Low override this resolver.
        if (blastRadius == BlastRadius.Trivial)
        {
            return AutoApprovePipeline;
        }

        return StandardPipeline;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ResolveWithDecision(
        ChangeTargetKind targetKind,
        BlastRadius blastRadius,
        AutonomyDecisionResult? decision)
    {
        // No decision supplied — fall back to the static PR-2 rule so existing
        // call sites that don't compute a decision behave identically.
        if (decision is null)
        {
            return Resolve(targetKind, blastRadius);
        }

        // Critical is already double-guarded in the policy itself, but the
        // resolver re-asserts it here so a consumer-supplied IAutonomyDecisionEvaluator
        // that bypassed the policy cannot drop approval at Critical.
        if (blastRadius == BlastRadius.Critical)
        {
            return StandardPipeline;
        }

        return decision.Decision == AutonomyDecision.AutoApprove
            ? AutoApprovePipeline
            : StandardPipeline;
    }
}
