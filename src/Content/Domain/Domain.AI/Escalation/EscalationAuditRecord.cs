namespace Domain.AI.Escalation;

/// <summary>
/// A single audit log entry for an escalation lifecycle event.
/// Used by <c>IEscalationAuditStore</c> for append-only JSONL persistence.
/// The <see cref="Payload"/> field contains the serialized event data,
/// discriminated by <see cref="RecordType"/>.
/// </summary>
public sealed record EscalationAuditRecord
{
    /// <summary>Discriminator for deserialization of <see cref="Payload"/>.</summary>
    public required EscalationAuditRecordType RecordType { get; init; }

    /// <summary>Correlates to the originating escalation.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>When this audit record was created.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Serialized JSON of the request, decision, or outcome depending on <see cref="RecordType"/>.
    /// </summary>
    /// <remarks>
    /// Deserialization target by <see cref="RecordType"/>:
    /// <list type="bullet">
    /// <item><see cref="EscalationAuditRecordType.Request"/> → <see cref="EscalationRequest"/></item>
    /// <item><see cref="EscalationAuditRecordType.Decision"/> → <see cref="ApproverDecision"/></item>
    /// <item><see cref="EscalationAuditRecordType.Outcome"/> → <see cref="EscalationOutcome"/></item>
    /// </list>
    /// </remarks>
    public required string Payload { get; init; }
}
