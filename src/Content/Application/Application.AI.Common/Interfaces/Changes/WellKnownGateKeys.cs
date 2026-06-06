namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// Stable string keys for the four built-in gates. Used by the orchestrator to
/// partition a proposal's <c>RequiredGates</c> into validation, approval, and
/// merge phases and to drive the state-machine transitions accordingly.
/// </summary>
/// <remarks>
/// Consumer-defined gates can use any key; only these four are special-cased by
/// the orchestrator. If <see cref="Approval"/> is absent from a proposal's
/// <c>RequiredGates</c>, the orchestrator skips <c>AwaitingApproval</c> entirely
/// and transitions validation directly into the merge phase — matching the
/// auto-approve case under the autonomous tier.
/// </remarks>
public static class WellKnownGateKeys
{
    /// <summary>
    /// The validation gate that runs declared validators (tests, lint, dry-run)
    /// for the proposal's target type. Belongs to the validation phase.
    /// </summary>
    public const string SelfValidation = "self_validation";

    /// <summary>
    /// The policy gate that runs declared policies (Checkov, OPA, etc.). Belongs
    /// to the validation phase.
    /// </summary>
    public const string Policy = "policy";

    /// <summary>
    /// The approval gate. Transitions the proposal to <c>AwaitingApproval</c>;
    /// resumed only via <c>ApproveChangeProposalCommand</c> or
    /// <c>RejectChangeProposalCommand</c>.
    /// </summary>
    public const string Approval = "approval";

    /// <summary>
    /// The merge gate — the only mutator in the pipeline. Drives the proposal
    /// to <c>Merged</c> on success or terminal <c>Rejected</c> on failure.
    /// </summary>
    public const string Merge = "merge";
}
