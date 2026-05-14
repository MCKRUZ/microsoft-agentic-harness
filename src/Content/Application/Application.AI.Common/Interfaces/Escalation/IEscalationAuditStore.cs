using Domain.AI.Escalation;

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
    /// Returns the full audit history for a specific escalation, ordered chronologically.
    /// Returns an empty list if the escalation ID is unknown.
    /// </summary>
    Task<IReadOnlyList<EscalationAuditRecord>> GetHistoryAsync(Guid escalationId, CancellationToken ct);
}
