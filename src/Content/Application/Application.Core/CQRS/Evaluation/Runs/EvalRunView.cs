using Domain.AI.Evaluation;
using Domain.AI.Runs;

namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>
/// One evaluation run as its owner sees it: the substrate's record, what it was asked to evaluate, and
/// the report if it produced one.
/// </summary>
/// <remarks>
/// Three pieces rather than one because they are stored in three places for good reasons — the record
/// is kind-agnostic, the request and report belong to this kind alone. Joining them here rather than at
/// the transport keeps the "a run with no submission is still a readable run" rule in one place instead
/// of in every surface that reads one.
/// </remarks>
public sealed record EvalRunView
{
    /// <summary>The run's identity, ownership and lifecycle.</summary>
    public required RunRecord Run { get; init; }

    /// <summary>
    /// The datasets the run was asked to evaluate. Empty when the submission has already been
    /// reclaimed, which is possible for a run whose own record has not been swept yet.
    /// </summary>
    public IReadOnlyList<string> DatasetNames { get; init; } = [];

    /// <summary>The report, once the run has produced one.</summary>
    public EvalRunReport? Report { get; init; }
}
