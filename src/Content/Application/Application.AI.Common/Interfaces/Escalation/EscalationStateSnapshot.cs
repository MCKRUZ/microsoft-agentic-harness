using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// A point-in-time view of one durably persisted escalation, as returned by
/// <see cref="IEscalationStateStore.GetActiveAsync"/>. Carries everything the escalation
/// service needs to rehydrate a pending escalation after a restart (request, collected
/// decisions, original creation instant for timeout resumption) or to finalize a stuck
/// resolution (the pending outcome).
/// </summary>
public sealed record EscalationStateSnapshot
{
    /// <summary>The original escalation request, exactly as it was created.</summary>
    public required EscalationRequest Request { get; init; }

    /// <summary>
    /// The approver decisions collected so far, in submission order. Empty for an
    /// escalation nobody has decided on yet.
    /// </summary>
    public required IReadOnlyList<ApproverDecision> Decisions { get; init; }

    /// <summary>
    /// When the escalation was created by the service. Timeout resumption after a restart
    /// is computed from this instant, so downtime counts against the escalation's timeout
    /// budget rather than resetting it.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The persisted lifecycle status of the record.</summary>
    public required EscalationPersistedStatus Status { get; init; }

    /// <summary>
    /// The resolved outcome, present only when <see cref="Status"/> is
    /// <see cref="EscalationPersistedStatus.ResolvedPendingAudit"/> (awaiting the audit
    /// re-drive) — <see cref="EscalationPersistedStatus.Pending"/> records have none.
    /// </summary>
    public EscalationOutcome? Outcome { get; init; }
}
