namespace Domain.AI.Escalation;

/// <summary>
/// Categorizes what happened when an approver decision was submitted via
/// <c>IEscalationService.SubmitDecisionAsync</c>. Each value is a distinct,
/// externally observable outcome that a transport layer (HTTP, hub, console)
/// can map without ambiguity — replacing the earlier design where three of
/// these cases were conflated into a single <c>null</c> return.
/// </summary>
public enum EscalationDecisionStatus
{
    /// <summary>
    /// No escalation with the given ID is currently pending. The decision was not
    /// recorded. Maps naturally to HTTP 404.
    /// </summary>
    UnknownEscalation = 0,

    /// <summary>
    /// The submitting identity is not on the escalation's approver roster. The
    /// decision was rejected before being recorded or evaluated. Maps naturally
    /// to HTTP 403.
    /// </summary>
    ApproverNotAuthorized,

    /// <summary>
    /// The decision was durably recorded but did not resolve the escalation:
    /// either the approval strategy requires further decisions (e.g. AllOf with
    /// approvers still pending), or another decision resolved the escalation
    /// concurrently. Also returned as an idempotent echo when the same approver
    /// repeats a decision with the same verdict — the first submission already
    /// speaks for them. Poll <c>IEscalationService.GetOutcomeAsync</c> for the
    /// final verdict. Maps naturally to HTTP 202.
    /// </summary>
    DecisionRecorded,

    /// <summary>
    /// The submitting approver already has a recorded decision on this escalation with the
    /// <em>opposite</em> verdict; the new decision was rejected, not recorded. Changing a vote
    /// is not supported over this surface — silently discarding the change while reporting it
    /// recorded would be dishonest, and silently flipping it would let a replayed request alter
    /// an audit-final decision. Maps naturally to HTTP 409.
    /// </summary>
    ConflictingDecision,

    /// <summary>
    /// This decision resolved the escalation. <see cref="EscalationDecisionResult.Outcome"/>
    /// carries the final <see cref="EscalationOutcome"/>. Maps naturally to HTTP 200.
    /// </summary>
    Resolved,

    /// <summary>
    /// The escalation already reached a resolution that could not be durably recorded (the
    /// compliance audit or durable-state write failed), so it is parked awaiting an operator
    /// reconcile pass. This decision was <em>not</em> recorded and did not participate — the
    /// verdict was already decided before it arrived. Reporting
    /// <see cref="DecisionRecorded"/> here would tell an approver their vote counted when it
    /// was discarded. Poll <c>IEscalationService.GetOutcomeAsync</c>; the verdict becomes
    /// observable once reconciliation completes. Maps naturally to HTTP 409 or 503.
    /// </summary>
    AwaitingReconciliation
}
