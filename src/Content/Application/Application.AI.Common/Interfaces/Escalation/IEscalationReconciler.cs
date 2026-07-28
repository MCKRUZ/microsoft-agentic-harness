namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Operator-facing recovery for escalations stuck in the "resolved but never audited" state.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this closes:</b> the escalation service's outcome audit write is fail-closed —
/// when <see cref="IEscalationAuditStore.RecordOutcomeAsync"/> throws during decide, the
/// decision is not reported accepted and the escalation deliberately stays in the active set.
/// Before this interface existed, such an escalation was permanently wedged: every further
/// decision echoed "recorded" without effect, the timeout had already fired or been consumed,
/// and no operator surface could push it to completion.
/// </para>
/// <para>
/// <see cref="ReconcileStuckEscalationsAsync"/> detects both stuck shapes — in-memory states
/// whose resolution faulted on the audit write, and (when durable escalation state is enabled)
/// persisted <c>ResolvedPendingAudit</c> records left behind by a crash — and re-drives the
/// audit write for each. It is idempotent: an escalation is finalized at most once, a pass over
/// a healthy system recovers nothing, and a pass while the audit store is still down simply
/// reports the records as still stuck. Re-driving after a crash <i>may</i> append a duplicate
/// outcome line to the append-only audit (when the crash landed between the audit write and the
/// durable finalize) — a duplicate compliance record of the same verdict is the deliberate,
/// safe trade against losing the verdict.
/// </para>
/// <para>
/// This is intentionally a service-level mechanism, not an HTTP route: operators invoke it from
/// host tooling (console command, scheduled job, or admin script) via DI.
/// </para>
/// </remarks>
public interface IEscalationReconciler
{
    /// <summary>
    /// Scans for escalations whose resolution was reached but never durably audited, re-drives
    /// the audit write for each, and finalizes the ones that succeed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Which escalations were recovered and which remain stuck.</returns>
    Task<EscalationReconcileResult> ReconcileStuckEscalationsAsync(CancellationToken ct);
}
