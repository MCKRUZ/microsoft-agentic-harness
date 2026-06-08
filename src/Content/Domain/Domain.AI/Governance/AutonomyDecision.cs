namespace Domain.AI.Governance;

/// <summary>
/// The graded-autonomy decision for a single proposal context. Independent of the
/// agent's <see cref="AutonomyLevel"/> tier — the tier sets the baseline; the
/// decision narrows it per <see cref="Domain.AI.Changes.BlastRadius"/>,
/// <see cref="Domain.AI.Changes.ChangeTargetKind"/>, and the state-change flag.
/// </summary>
/// <remarks>
/// <para>
/// Three values intentionally — the autonomy tier enum already covers "Restricted"
/// (everything asks). What an evaluator returns is the outcome for one proposal:
/// auto-approve, route through human review, or reject outright (the latter is
/// reserved for misconfig-or-policy-overlap cases where the evaluator wants to
/// stop the proposal cold without involving the approval router).
/// </para>
/// <para>
/// The decision is consulted by <c>IChangeProposalGateResolver</c> when deciding
/// whether to include the approval gate in a proposal's frozen gate list; it is
/// also surfaced via <see cref="AutonomyDecisionResult"/> for audit.
/// </para>
/// </remarks>
public enum AutonomyDecision
{
    /// <summary>
    /// The proposal may proceed without an approval gate. The <c>ApprovalGate</c>
    /// is omitted from the frozen gate list. Reserved for low-risk, non-state-changing
    /// proposals under a permissive tier in a permissive environment.
    /// </summary>
    AutoApprove = 0,

    /// <summary>
    /// The proposal must be routed through the approval gate. Default for anything
    /// that touches state, anything above <see cref="Domain.AI.Changes.BlastRadius.Low"/>
    /// blast radius unless explicitly overridden, and everything in stricter environments.
    /// </summary>
    RequiresApproval = 1,

    /// <summary>
    /// The configuration explicitly forbids the proposal. The orchestrator transitions
    /// the proposal to Rejected without invoking gates. Useful for hard-coded
    /// "Critical in Production by this skill is always rejected" policies that pre-empt
    /// the approval router entirely.
    /// </summary>
    Forbidden = 2
}
