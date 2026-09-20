using Domain.AI.Governance;
using Domain.Common;
using Domain.Common.Constants;
using MediatR;

namespace Application.Core.CQRS.Governance;

/// <summary>
/// Retrieves governance audit records — the append-only, hash-chained trail of tool-call
/// allow/deny/warn decisions, prompt-injection blocks, classification outcomes, and delegation
/// events. All filters are optional; results are capped at <see cref="MaxResults"/> most-recent
/// records.
/// </summary>
public sealed record GetGovernanceAuditsQuery : IRequest<Result<IReadOnlyList<GovernanceAuditRecord>>>
{
    /// <summary>Start of the query window (inclusive). Null leaves the window open on the left.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the query window (inclusive). Null leaves the window open on the right.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>Filter by the agent whose action was evaluated.</summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// Maximum number of records to return, between 1 and
    /// <see cref="AuditQueryDefaults.MaxResults"/>. When more records match, the most recent ones
    /// are returned (still in chronological order).
    /// </summary>
    public int MaxResults { get; init; } = AuditQueryDefaults.DefaultResults;
}
