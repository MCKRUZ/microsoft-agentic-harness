using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Orchestrates the escalation lifecycle: creation, notification dispatch,
/// approval tracking, timeout management, and outcome resolution.
/// </summary>
/// <remarks>
/// Two consumption modes are supported:
/// <list type="bullet">
///   <item><description><see cref="RequestEscalationAsync"/> -- blocking; caller awaits the human decision.</description></item>
///   <item><description><see cref="QueueEscalationAsync"/> -- non-blocking; returns an ID for later polling.</description></item>
/// </list>
/// The mode is selected by the agent's <c>EscalationWaitBehavior</c> (Block vs. QueueAndContinue),
/// resolved from the autonomy tier policy.
/// </remarks>
public interface IEscalationService
{
    /// <summary>
    /// Creates an escalation and blocks until a human decision resolves it or the timeout expires.
    /// </summary>
    Task<EscalationOutcome> RequestEscalationAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>
    /// Creates an escalation without blocking. Returns the escalation ID for later polling.
    /// </summary>
    Task<Guid> QueueEscalationAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>
    /// Submits an approver's decision and reports what happened as a discriminated
    /// <see cref="EscalationDecisionResult"/>:
    /// <see cref="EscalationDecisionStatus.UnknownEscalation"/> (no such pending escalation),
    /// <see cref="EscalationDecisionStatus.ApproverNotAuthorized"/> (identity not on the roster),
    /// <see cref="EscalationDecisionStatus.DecisionRecorded"/> (recorded, escalation still unresolved), or
    /// <see cref="EscalationDecisionStatus.Resolved"/> (this decision resolved it; the result
    /// carries the final <see cref="EscalationOutcome"/>).
    /// </summary>
    Task<EscalationDecisionResult> SubmitDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct);

    /// <summary>
    /// Returns the pending escalation request, or null if resolved or unknown.
    /// </summary>
    Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct);

    /// <summary>
    /// Returns the resolved <see cref="EscalationOutcome"/> for an escalation, or null if it is
    /// still pending or unknown.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GetPendingEscalationAsync"/> — which reports only whether a request is
    /// still open — this reports the final verdict (approved, denied, timed out, or escalated) once
    /// the escalation has resolved. It is the query the plan executor uses on resume to decide the
    /// fate of a step parked in <c>Blocked</c> awaiting a human decision: an approved outcome lets
    /// the step complete and release its downstream, a non-approved outcome fails it. A null result
    /// means "no decision yet" and the step remains blocked.
    /// </remarks>
    Task<EscalationOutcome?> GetOutcomeAsync(Guid escalationId, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="escalationId"/> is the most recent escalation queued for
    /// <paramref name="binding"/>'s plan step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A binding names a step, not an individual escalation, and one step can raise several in turn
    /// (an escalate-on-failure step that is approved, re-run and fails again). The plan state store keeps
    /// only the id the step is waiting on, so repointing it at an older, already-approved escalation for
    /// the same step would release the step while the newer escalation is still pending or was denied.
    /// This is the anchor that closes that: the service records, outside the plan state store, which
    /// escalation was queued last for each step, and the plan executor releases a step only on that one
    /// (#863).
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> An escalation the service has no record of for this step (queued before this
    /// check existed, or pruned) is not the latest. The durable store, when enabled, is authoritative so a
    /// second host or a restart sees the same answer; the in-process record answers when it is not.
    /// </para>
    /// </remarks>
    /// <param name="escalationId">The escalation the step is waiting on.</param>
    /// <param name="binding">The plan step the escalation was queued for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsLatestForBindingAsync(Guid escalationId, EscalationBinding binding, CancellationToken ct);

    /// <summary>
    /// Returns all pending escalations assigned to a specific approver.
    /// </summary>
    Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(string approverName, CancellationToken ct);

    /// <summary>
    /// Explicitly cancels a pending escalation. Used for agent disconnects,
    /// admin force-resolve, or governance context changes.
    /// </summary>
    /// <param name="escalationId">The pending escalation to cancel.</param>
    /// <param name="reason">Free-text reason for the cancellation.</param>
    /// <param name="cancelledBy">
    /// The identity performing the cancellation (a token-derived approver name over HTTP, or a
    /// system actor such as <c>"system:agent-disconnect"</c> for internal callers). Stamped on
    /// the resulting <see cref="EscalationOutcome.CancelledBy"/> and therefore recorded in the
    /// durable outcome audit — a force-denial must always be attributable.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<EscalationOutcome> CancelEscalationAsync(Guid escalationId, string reason, string cancelledBy, CancellationToken ct);
}
