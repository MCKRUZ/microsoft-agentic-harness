using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Shared append logic for operator-action audit records so both drift write commands record
/// caller identity identically, in two phases with deliberately different failure postures.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two records.</b> The threat model names history poisoning as the risk and this audit
/// trail as the compensating control, so the control must hold when the store is under attack.
/// Recording only after the mutation makes the trail fail-<em>open</em>: anyone who can make the
/// audit store fail (fill the disk, revoke write permission on <c>AuditPath</c>) gets
/// unattributed poisoning — pushes still advance EWMA, still persist, still return success, and
/// nothing lands in the trail. <c>DefaultEscalationService</c> can be fail-closed because it
/// audits the request <em>before</em> mutating; this recorder mirrors that ordering rather than
/// its posture alone.
/// </para>
/// <para>
/// <see cref="RecordAttemptAsync"/> therefore runs before dispatch and is fail-CLOSED: its
/// result gates the write. <see cref="RecordOutcomeAsync"/> runs after and is fail-OPEN, which
/// is sound precisely because the attempt record already exists — the mutation has happened and
/// is already attributable, so refusing to report it would lose information rather than protect
/// any.
/// </para>
/// </remarks>
internal static class DriftOperatorAuditRecorder
{
    /// <summary>
    /// Appends the pre-dispatch attempt record. <b>Fail-closed</b>: callers must abort the write
    /// when this returns a failure, so no drift mutation can occur without a durable record of
    /// who requested it.
    /// </summary>
    /// <param name="auditStore">The append-only drift audit store.</param>
    /// <param name="recordType">The operator-action record type being appended.</param>
    /// <param name="audit">The caller envelope; its phase must be <see cref="DriftOperatorActionPhase.Attempt"/>.</param>
    /// <param name="recordedAt">The append timestamp.</param>
    /// <param name="logger">Logger for append diagnostics.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the attempt is durably recorded; otherwise a failure the caller must honour.</returns>
    public static async Task<Result> RecordAttemptAsync(
        IDriftAuditStore auditStore,
        DriftAuditRecordType recordType,
        DriftOperatorActionAudit audit,
        DateTimeOffset recordedAt,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var result = await auditStore.RecordAsync(new DriftAuditRecord
            {
                RecordId = Guid.NewGuid(),
                // Attempt records correlate by ActionId; the produced artifact does not exist yet.
                EventId = audit.ActionId,
                RecordType = recordType,
                Payload = audit.ToJson(),
                RecordedAt = recordedAt
            }, ct);

            if (result.IsSuccess)
                return Result.Success();

            logger.LogError(
                "Refusing drift write: attempt audit could not be recorded ({Action} by {CallerId}, action {ActionId}): {Errors}",
                audit.Action, audit.CallerId, audit.ActionId, string.Join(", ", result.Errors));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Refusing drift write: attempt audit could not be recorded ({Action} by {CallerId}, action {ActionId})",
                audit.Action, audit.CallerId, audit.ActionId);
        }

        // Stable, scrubbed code — the store's error text never reaches the caller.
        return Result.Fail("drift.audit_unavailable");
    }

    /// <summary>
    /// Appends the post-dispatch outcome record. <b>Fail-open</b>: append failures are logged
    /// but never fail the operation, because the mutation has already happened and the
    /// fail-closed attempt record already made it attributable.
    /// </summary>
    /// <param name="auditStore">The append-only drift audit store.</param>
    /// <param name="recordType">The operator-action record type being appended.</param>
    /// <param name="audit">The caller/outcome envelope; its phase must be <see cref="DriftOperatorActionPhase.Outcome"/>.</param>
    /// <param name="eventId">Correlation id for the record (the produced artifact's id, or the action id on failure).</param>
    /// <param name="recordedAt">The append timestamp.</param>
    /// <param name="logger">Logger for append diagnostics.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task RecordOutcomeAsync(
        IDriftAuditStore auditStore,
        DriftAuditRecordType recordType,
        DriftOperatorActionAudit audit,
        Guid eventId,
        DateTimeOffset recordedAt,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var result = await auditStore.RecordAsync(new DriftAuditRecord
            {
                RecordId = Guid.NewGuid(),
                EventId = eventId,
                RecordType = recordType,
                Payload = audit.ToJson(),
                RecordedAt = recordedAt
            }, ct);

            if (!result.IsSuccess)
            {
                logger.LogError(
                    "Failed to append drift outcome audit ({Action} by {CallerId}, action {ActionId}): {Errors}. The attempt record remains as the attribution of record.",
                    audit.Action, audit.CallerId, audit.ActionId, string.Join(", ", result.Errors));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Failed to append drift outcome audit ({Action} by {CallerId}, action {ActionId}). The attempt record remains as the attribution of record.",
                audit.Action, audit.CallerId, audit.ActionId);
        }
    }
}
