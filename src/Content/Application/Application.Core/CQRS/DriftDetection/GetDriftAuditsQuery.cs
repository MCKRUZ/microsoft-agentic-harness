using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Retrieves drift audit records — the append-only, hash-chained trail of detections,
/// resolutions, baseline updates, escalation triggers, and operator actions (evaluation pushes
/// and recalculation requests with their caller identities). All filters are optional; results
/// are capped at <see cref="MaxResults"/> most-recent records.
/// </summary>
public sealed record GetDriftAuditsQuery : IRequest<Result<IReadOnlyList<DriftAuditRecord>>>
{
    /// <summary>Start of the query window (inclusive). Null leaves the window open on the left.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window (inclusive). Null leaves the window open on the right.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter by audit record type.</summary>
    public DriftAuditRecordType? RecordType { get; init; }

    /// <summary>Filter by originating drift event ID.</summary>
    public Guid? EventId { get; init; }

    /// <summary>
    /// Maximum number of records to return, between 1 and
    /// <see cref="DriftValidationRules.MaxAuditResults"/>. When more records match, the
    /// most recent ones are returned (still in chronological order).
    /// </summary>
    public int MaxResults { get; init; } = DriftValidationRules.DefaultAuditResults;
}
