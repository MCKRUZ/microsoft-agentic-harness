namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Lifecycle status of a durably persisted escalation record in the governance-state store.
/// Distinct from the in-memory resolution flags: this status exists to make the
/// "resolved but not yet durably audited" window detectable and recoverable after a crash.
/// </summary>
public enum EscalationPersistedStatus
{
    /// <summary>
    /// The escalation is open: decidable, listable, and cancellable. Rehydrated into the
    /// active in-memory set on startup.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// A resolution (decision, timeout, or cancellation) was reached and durably recorded,
    /// but the fail-closed compliance audit write did not complete. The escalation must not
    /// be reported as resolved; it is the stuck state the
    /// <see cref="IEscalationReconciler"/> detects and re-drives once the audit store
    /// recovers.
    /// </summary>
    ResolvedPendingAudit = 1,

    /// <summary>
    /// Terminal: the resolution has been durably audited. The outcome is queryable via
    /// <see cref="IEscalationStateStore.GetResolvedOutcomeAsync"/> even after a restart.
    /// </summary>
    Resolved = 2,

    /// <summary>
    /// A reconcile pass has claimed this record and is re-driving its audit write. The claim
    /// is what stops two concurrent passes — a timer tick and an operator-triggered run —
    /// from both writing the compliance line and both firing the resolution notification.
    /// A pass that crashes mid-claim leaves the record here; the next pass's
    /// <see cref="IEscalationStateStore.ReleaseClaimAsync"/> sweep returns it to
    /// <see cref="ResolvedPendingAudit"/> so it stays recoverable rather than stranded.
    /// </summary>
    AuditInFlight = 3
}
