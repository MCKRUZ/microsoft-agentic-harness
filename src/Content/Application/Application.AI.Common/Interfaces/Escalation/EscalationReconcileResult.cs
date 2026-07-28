namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// The outcome of one <see cref="IEscalationReconciler.ReconcileStuckEscalationsAsync"/> pass.
/// </summary>
public sealed record EscalationReconcileResult
{
    /// <summary>
    /// Escalations whose stuck resolution was successfully finalized this pass: the audit
    /// write was re-driven, the durable record moved to terminal, and the outcome became
    /// observable to pollers.
    /// </summary>
    public required IReadOnlyList<Guid> Recovered { get; init; }

    /// <summary>
    /// Escalations that were detected as stuck but could not be finalized (typically the
    /// audit store is still failing). They remain in the stuck state and will be picked up
    /// by a future reconcile pass; nothing is lost.
    /// </summary>
    public required IReadOnlyList<Guid> StillStuck { get; init; }
}
