namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// Opts a <see cref="Interfaces.ILlmJudge.JudgeAsync"/> call into the strict verdict
/// contract: a failing score must cite the exact rubric text it says was violated, and the
/// judge is rejected and retried when it doesn't. Absent (<c>null</c> on
/// <see cref="LlmJudgeRequest.VerdictContract"/>) preserves today's <c>{score, reasoning}</c>
/// contract exactly — this is what keeps the RAG judge metric pack byte-identical without
/// any changes of their own.
/// </summary>
/// <remarks>
/// See <c>ViolatedClauseVerifier</c> (Infrastructure.AI.Evaluation) for the check itself.
/// This record only carries what the check needs; it has no knowledge of the metric or
/// case that produced it.
/// </remarks>
public sealed record JudgeVerdictContract
{
    /// <summary>
    /// The text a cited <c>violated_clause</c> must appear within, verbatim (after
    /// whitespace normalization). Normally the rubric text the case author supplied —
    /// passed explicitly because <see cref="Interfaces.ILlmJudge"/> is metric-agnostic and
    /// has no way to know which caller variable holds the rubric.
    /// </summary>
    public required string ClauseSource { get; init; }

    /// <summary>
    /// Scores strictly below this value are "failing verdicts" that must cite a violated
    /// clause. Callers pass the metric's own <c>MetricSpec.Threshold</c>.
    /// </summary>
    public required double FailingBelow { get; init; }

    /// <summary>
    /// Minimum character length a cited clause must have to count as identifying a specific
    /// requirement, rather than a trivially short fragment (e.g. "the") that would satisfy a
    /// naive substring check without saying anything.
    /// </summary>
    public int MinClauseLength { get; init; } = 12;
}
