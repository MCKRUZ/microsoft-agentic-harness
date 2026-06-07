using Domain.AI.Changes;
using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// Derives the default ordered list of gate keys for a proposal from its
/// <see cref="ChangeTarget.Kind"/> and its <see cref="BlastRadius"/>. Called by
/// <c>SubmitChangeProposalCommand</c> when the caller does not explicitly supply
/// <c>RequiredGates</c>.
/// </summary>
/// <remarks>
/// <para>
/// The default map ships with sensible defaults — typically <c>self_validation →
/// policy → approval → merge</c> for any non-trivial target — but a consumer can
/// register a different <see cref="IChangeProposalGateResolver"/> implementation
/// to enforce stricter pipelines (an extra <c>compliance</c> gate for regulated
/// environments, or an extra <c>cost</c> gate for IaC at high blast radius).
/// </para>
/// <para>
/// The resolver is the right place to enforce "Critical blast radius always
/// requires Approval even when the configured autonomy tier would auto-approve"
/// because the resolver runs before the orchestrator and the resulting gate list
/// is baked into the proposal's <c>RequiredGates</c> — it cannot be lowered later.
/// </para>
/// </remarks>
public interface IChangeProposalGateResolver
{
    /// <summary>
    /// Compute the ordered gate-key list for a proposal with the given target kind
    /// and blast radius. Pre-PR-4 entry point — equivalent to calling
    /// <see cref="ResolveWithDecision"/> with a null decision, which forces the
    /// resolver into its "no graded-autonomy input" fallback path.
    /// </summary>
    /// <param name="targetKind">The proposal's target kind.</param>
    /// <param name="blastRadius">The proposal's estimated blast radius.</param>
    /// <returns>An ordered list of gate keys to be assigned to <c>ChangeProposal.RequiredGates</c>. Must not be empty.</returns>
    IReadOnlyList<string> Resolve(ChangeTargetKind targetKind, BlastRadius blastRadius);

    /// <summary>
    /// PR-4 entry point: compute the ordered gate-key list with an attached
    /// <see cref="AutonomyDecisionResult"/> the resolver may consult to decide
    /// whether to include the approval gate.
    /// </summary>
    /// <param name="targetKind">The proposal's target kind.</param>
    /// <param name="blastRadius">The proposal's estimated blast radius.</param>
    /// <param name="decision">
    /// Pre-computed graded-autonomy decision. May be null when the caller has
    /// not (or could not) compute one — in that case the resolver behaves
    /// exactly as if <see cref="Resolve(ChangeTargetKind, BlastRadius)"/> were
    /// called.
    /// </param>
    /// <returns>An ordered list of gate keys. Must not be empty.</returns>
    /// <remarks>
    /// Default implementation forwards to the non-decision overload so existing
    /// resolvers (including test stubs) keep working without modification. New
    /// resolvers that want to honour the decision override this method.
    /// </remarks>
    IReadOnlyList<string> ResolveWithDecision(
        ChangeTargetKind targetKind,
        BlastRadius blastRadius,
        AutonomyDecisionResult? decision)
        => Resolve(targetKind, blastRadius);
}
