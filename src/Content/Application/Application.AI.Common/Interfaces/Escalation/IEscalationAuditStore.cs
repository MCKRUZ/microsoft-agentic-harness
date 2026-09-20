using Domain.AI.Escalation;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Append-only audit store for escalation lifecycle events.
/// Records requests, individual approver decisions, and final outcomes
/// as <see cref="EscalationAuditRecord"/> entries for compliance.
/// </summary>
/// <remarks>
/// The default implementation writes JSONL (one JSON object per line) with file
/// locking, following the same pattern as <c>JsonlDelegationStore</c> from Phase 1.
/// Each record includes a <c>RecordType</c> discriminator for deserialization.
/// </remarks>
public interface IEscalationAuditStore
{
    /// <summary>Records that an escalation was created.</summary>
    Task RecordRequestAsync(EscalationRequest request, CancellationToken ct);

    /// <summary>Records an individual approver's decision.</summary>
    Task RecordDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct);

    /// <summary>Records the final outcome of an escalation.</summary>
    Task RecordOutcomeAsync(EscalationOutcome outcome, CancellationToken ct);

    /// <summary>
    /// Records what happened when an approved escalation's action was actually carried out.
    /// Same fail-closed throw semantics as the other <c>Record*</c> methods here — whether a
    /// failure to record should still be reported to the approver is the caller's decision, not
    /// this store's.
    /// </summary>
    Task RecordExecutionAsync(EscalationExecutionRecord record, CancellationToken ct);

    /// <summary>
    /// Returns the full audit history for a specific escalation, ordered chronologically.
    /// Returns an empty list if the escalation ID is unknown.
    /// </summary>
    Task<IReadOnlyList<EscalationAuditRecord>> GetHistoryAsync(Guid escalationId, CancellationToken ct);

    /// <summary>
    /// Returns the most recently recorded execution outcome for an escalation (#396) — what
    /// happened when the approved action actually ran, as reported through
    /// <see cref="IApprovalExecutionReporter"/>. Null if the escalation is unknown, was never
    /// approved, or its approved action has not been reported yet.
    /// </summary>
    Task<EscalationExecutionRecord?> GetLatestExecutionAsync(Guid escalationId, CancellationToken ct);

    /// <summary>
    /// Queries escalation audit records across the whole trail, or a time-windowed/type-filtered
    /// slice of it — unlike <see cref="GetHistoryAsync"/>, the escalation id filter is optional
    /// here (#714, prerequisite for compliance reporting, #696). Returns <see cref="Result{T}"/>
    /// rather than a bare list, since this member backs a compliance report that must be able to
    /// tell "zero matches" apart from "the read failed".
    /// </summary>
    /// <param name="query">Filters narrowing which records to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<IReadOnlyList<EscalationAuditRecord>>> QueryAsync(
        EscalationAuditQuery query, CancellationToken cancellationToken);
}
