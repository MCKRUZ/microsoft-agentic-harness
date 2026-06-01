using Application.AI.Common.Evaluation.Models;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Evaluation.GetEvalRunHistory;

/// <summary>
/// Returns the most recent eval runs the dashboard has ingested, projected to
/// <see cref="EvalRunSummary"/> so list pages don't pay for per-case payloads.
/// </summary>
/// <remarks>
/// Used by the dashboard's run-history page. Ordered descending by
/// <see cref="Domain.AI.Evaluation.EvalRunReport.StartedAtUtc"/>.
/// </remarks>
public sealed record GetEvalRunHistoryQuery : IRequest<Result<IReadOnlyList<EvalRunSummary>>>
{
    /// <summary>Maximum number of runs to return. Defaults to 50.</summary>
    public int Take { get; init; } = 50;
}
