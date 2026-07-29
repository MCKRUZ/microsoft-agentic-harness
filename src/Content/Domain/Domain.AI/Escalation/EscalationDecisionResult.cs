namespace Domain.AI.Escalation;

/// <summary>
/// The discriminated result of submitting an approver decision via
/// <c>IEscalationService.SubmitDecisionAsync</c>: a <see cref="Status"/> naming
/// which of the four distinct outcomes occurred, plus the resolved
/// <see cref="Outcome"/> when — and only when — this decision resolved the escalation.
/// </summary>
/// <remarks>
/// This type replaces the previous <c>EscalationOutcome?</c> return, whose
/// <c>null</c> conflated three different situations (unknown escalation,
/// non-roster approver, and decision-recorded-but-still-pending). A control
/// plane exposing decisions over HTTP must distinguish those cases (404 / 403 /
/// 202 respectively); collapsing them into <c>null</c> made that impossible.
/// Construct instances via the static factories so the
/// status/outcome pairing invariant (outcome present iff
/// <see cref="EscalationDecisionStatus.Resolved"/>) always holds.
/// </remarks>
public sealed record EscalationDecisionResult
{
    /// <summary>What happened to the submitted decision.</summary>
    public required EscalationDecisionStatus Status { get; init; }

    /// <summary>
    /// The final escalation verdict. Non-null if and only if <see cref="Status"/> is
    /// <see cref="EscalationDecisionStatus.Resolved"/>.
    /// </summary>
    public EscalationOutcome? Outcome { get; init; }

    // The outcome-less results are stateless and immutable, so a single shared
    // instance per status serves every call — the factories below hand these out.
    private static readonly EscalationDecisionResult UnknownInstance =
        new() { Status = EscalationDecisionStatus.UnknownEscalation };
    private static readonly EscalationDecisionResult NotAuthorizedInstance =
        new() { Status = EscalationDecisionStatus.ApproverNotAuthorized };
    private static readonly EscalationDecisionResult RecordedInstance =
        new() { Status = EscalationDecisionStatus.DecisionRecorded };
    private static readonly EscalationDecisionResult ConflictingInstance =
        new() { Status = EscalationDecisionStatus.ConflictingDecision };
    private static readonly EscalationDecisionResult AwaitingReconciliationInstance =
        new() { Status = EscalationDecisionStatus.AwaitingReconciliation };

    /// <summary>Creates a result for a decision targeting an escalation that is not pending.</summary>
    public static EscalationDecisionResult UnknownEscalation() => UnknownInstance;

    /// <summary>Creates a result for a decision rejected because the approver is not on the roster.</summary>
    public static EscalationDecisionResult ApproverNotAuthorized() => NotAuthorizedInstance;

    /// <summary>Creates a result for a decision that was recorded but left the escalation unresolved.</summary>
    public static EscalationDecisionResult DecisionRecorded() => RecordedInstance;

    /// <summary>
    /// Creates a result for a decision submitted against an escalation that already resolved but
    /// whose resolution could not be durably recorded, leaving it parked for reconciliation. The
    /// decision was not recorded and did not participate in the verdict.
    /// </summary>
    public static EscalationDecisionResult AwaitingReconciliation() => AwaitingReconciliationInstance;

    /// <summary>
    /// Creates a result for a decision rejected because the same approver already recorded the
    /// opposite verdict — votes cannot be changed over this surface.
    /// </summary>
    public static EscalationDecisionResult ConflictingDecision() => ConflictingInstance;

    /// <summary>Creates a result for a decision that resolved the escalation with the given verdict.</summary>
    /// <param name="outcome">The final resolved outcome. Must not be null.</param>
    public static EscalationDecisionResult Resolved(EscalationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new EscalationDecisionResult
        {
            Status = EscalationDecisionStatus.Resolved,
            Outcome = outcome
        };
    }
}
