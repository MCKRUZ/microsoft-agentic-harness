using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Evaluation.GetPromptVersionComparison;

/// <summary>
/// Aggregates eval-score signal per prompt version for a given prompt name and
/// metric. Powers the dashboard's "did the new prompt version actually do
/// better?" view.
/// </summary>
/// <remarks>
/// <para>
/// Implementation joins the prompt-usage log (which version was used on which
/// case) against the eval-result store (what score that case received on the
/// supplied metric). The result is a per-version aggregation suitable for a
/// bar chart or A/B sparkline.
/// </para>
/// </remarks>
public sealed record GetPromptVersionComparisonQuery
    : IRequest<Result<IReadOnlyList<PromptVersionComparisonRow>>>
{
    /// <summary>The registry name of the prompt to compare (e.g. <c>"faithfulness-judge"</c>).</summary>
    public required string PromptName { get; init; }
}
