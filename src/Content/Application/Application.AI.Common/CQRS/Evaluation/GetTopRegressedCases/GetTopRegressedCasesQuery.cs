using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Evaluation.GetTopRegressedCases;

/// <summary>
/// Returns the cases whose aggregated scores dropped the most between a baseline
/// run and a current run, ordered by largest negative delta first.
/// </summary>
/// <remarks>
/// <para>
/// Powers the dashboard's "what got worse?" view after a new prompt / model
/// rollout. Compares (case_id, metric_key) tuples that exist in both runs;
/// cases that only ran in one are excluded (they aren't regressions, they're
/// new or missing).
/// </para>
/// </remarks>
public sealed record GetTopRegressedCasesQuery
    : IRequest<Result<IReadOnlyList<RegressedCaseRow>>>
{
    /// <summary>The current (post-change) run identifier.</summary>
    public required string CurrentRunId { get; init; }

    /// <summary>The baseline (pre-change) run identifier to compare against.</summary>
    public required string BaselineRunId { get; init; }

    /// <summary>Maximum rows to return. Defaults to 20.</summary>
    public int Take { get; init; } = 20;
}
