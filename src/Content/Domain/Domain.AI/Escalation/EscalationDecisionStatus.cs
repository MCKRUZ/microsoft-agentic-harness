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
    /// concurrently. Poll <c>IEscalationService.GetOutcomeAsync</c> for the final
    /// verdict. Maps naturally to HTTP 202.
    /// </summary>
    DecisionRecorded,

    /// <summary>
    /// This decision resolved the escalation. <see cref="EscalationDecisionResult.Outcome"/>
    /// carries the final <see cref="EscalationOutcome"/>. Maps naturally to HTTP 200.
    /// </summary>
    Resolved
}
