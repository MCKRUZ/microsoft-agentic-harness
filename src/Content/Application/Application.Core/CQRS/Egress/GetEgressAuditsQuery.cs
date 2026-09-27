using Domain.AI.Egress;
using Domain.Common;
using Domain.Common.Constants;
using MediatR;

namespace Application.Core.CQRS.Egress;

/// <summary>
/// Retrieves egress audit records — the append-only, hash-chained trail of outbound network
/// policy decisions (allowed and denied alike). All filters are optional; results are capped at
/// <see cref="MaxResults"/> most-recent records.
/// </summary>
public sealed record GetEgressAuditsQuery : IRequest<Result<IReadOnlyList<EgressAuditRecord>>>
{
    /// <summary>Start of the query window (inclusive). Null leaves the window open on the left.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window (inclusive). Null leaves the window open on the right.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter by decision verdict (true = allowed, false = denied).</summary>
    public bool? Allowed { get; init; }

    /// <summary>Filter by target host.</summary>
    public string? Host { get; init; }

    /// <summary>
    /// Maximum number of records to return, between 1 and
    /// <see cref="AuditQueryDefaults.MaxResults"/>. When more records match, the most recent ones
    /// are returned (still in chronological order).
    /// </summary>
    public int MaxResults { get; init; } = AuditQueryDefaults.DefaultResults;
}
