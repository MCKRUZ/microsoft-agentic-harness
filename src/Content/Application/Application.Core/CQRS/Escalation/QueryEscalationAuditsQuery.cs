using Domain.AI.Escalation;
using Domain.Common;
using Domain.Common.Constants;
using MediatR;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Retrieves escalation audit records across the whole trail, or a time-windowed/type-filtered
/// slice of it — broader than <see cref="GetEscalationQuery"/>, which is keyed to one escalation.
/// All filters are optional; results are capped at <see cref="MaxResults"/> most-recent records.
/// </summary>
public sealed record QueryEscalationAuditsQuery : IRequest<Result<IReadOnlyList<EscalationAuditRecord>>>
{
    /// <summary>Start of the query window (inclusive). Null leaves the window open on the left.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window (inclusive). Null leaves the window open on the right.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter to a single escalation. Null matches every escalation.</summary>
    public Guid? EscalationId { get; init; }

    /// <summary>Filter by audit record type.</summary>
    public EscalationAuditRecordType? RecordType { get; init; }

    /// <summary>
    /// Maximum number of records to return, between 1 and
    /// <see cref="AuditQueryDefaults.MaxResults"/>. When more records match, the most recent ones
    /// are returned (still in chronological order).
    /// </summary>
    public int MaxResults { get; init; } = AuditQueryDefaults.DefaultResults;
}
