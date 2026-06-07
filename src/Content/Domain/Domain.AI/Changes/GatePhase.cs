namespace Domain.AI.Changes;

/// <summary>
/// The orchestrator phase a gate belongs to. Declared by each
/// <c>IChangeProposalGate</c> so the orchestrator can partition a proposal's
/// <c>RequiredGates</c> into validation vs. merge phase without string-matching
/// on a hardcoded approval-gate key.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline runs phases in order: <see cref="Validation"/> →
/// <see cref="Approval"/> (or auto-approve when no approval gate is present) →
/// <see cref="Merge"/> (the only phase that mutates state outside the pipeline).
/// Ordering inside a phase is taken from the order of <c>RequiredGates</c> —
/// the phase only controls which phase loop runs the gate, not when it runs
/// within that loop.
/// </para>
/// <para>
/// Future phases (<c>PostMerge</c> for verification, <c>PreValidation</c> for
/// shape checks) can be added when a concrete consumer needs them; deliberately
/// kept to three values today to match what the orchestrator actually runs.
/// </para>
/// </remarks>
public enum GatePhase
{
    /// <summary>
    /// Pre-approval checks. Self-validation, policy, compliance, custom domain
    /// validators. All must Pass before the approval phase runs.
    /// </summary>
    Validation = 0,

    /// <summary>
    /// The human-or-tier approval gate. At most one gate per proposal should
    /// declare this phase; the orchestrator routes Defer here through the
    /// <c>AwaitingApproval</c> status to wait for an out-of-band Approve / Reject
    /// command.
    /// </summary>
    Approval = 1,

    /// <summary>
    /// Post-approval mutators. The built-in <c>MergeGate</c> is the only one in
    /// the template; consumers may add post-merge verification gates here. A
    /// Defer in this phase appends history without a status transition (the
    /// state machine has no legal self-loop on <c>Merging</c>).
    /// </summary>
    Merge = 2
}
