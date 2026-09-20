using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Query DTO for <see cref="IEscalationAuditStore.QueryAsync"/> — a broader, time-windowed view
/// of the escalation audit trail than <see cref="IEscalationAuditStore.GetHistoryAsync"/>, which
/// is keyed to one escalation. All fields are optional filters.
/// </summary>
public sealed record EscalationAuditQuery
{
    /// <summary>Start of the query window (inclusive). Null leaves the window open on the left.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window (inclusive). Null leaves the window open on the right.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter to a single escalation. Null matches every escalation.</summary>
    public Guid? EscalationId { get; init; }

    /// <summary>Filter by audit record type.</summary>
    public EscalationAuditRecordType? RecordType { get; init; }
}
