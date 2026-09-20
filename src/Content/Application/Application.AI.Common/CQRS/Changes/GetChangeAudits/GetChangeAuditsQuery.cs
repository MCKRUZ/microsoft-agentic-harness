using Domain.AI.Changes;
using Domain.Common;
using Domain.Common.Constants;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.GetChangeAudits;

/// <summary>
/// Retrieves change-proposal audit records — the append-only, hash-chained trail of gate
/// decisions (pass/fail/defer) across the change-proposal pipeline. All filters are optional;
/// results are capped at <see cref="MaxResults"/> most-recent records.
/// </summary>
public sealed record GetChangeAuditsQuery : IRequest<Result<IReadOnlyList<ChangeAuditRecord>>>
{
    /// <summary>Start of the query window (inclusive). Null leaves the window open on the left.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window (inclusive). Null leaves the window open on the right.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter by the proposal this decision was made on.</summary>
    public string? ProposalId { get; init; }

    /// <summary>Filter by the gate that produced the decision.</summary>
    public string? GateKey { get; init; }

    /// <summary>Filter by the gate's verdict.</summary>
    public GateAction? Decision { get; init; }

    /// <summary>Filter by correlation id.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Maximum number of records to return, between 1 and
    /// <see cref="AuditQueryDefaults.MaxResults"/>. When more records match, the most
    /// recent ones are returned (still in chronological order).
    /// </summary>
    public int MaxResults { get; init; } = AuditQueryDefaults.DefaultResults;
}
