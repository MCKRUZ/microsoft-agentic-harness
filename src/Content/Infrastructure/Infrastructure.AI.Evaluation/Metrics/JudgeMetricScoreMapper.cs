using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Domain.AI.Evaluation;

namespace Infrastructure.AI.Evaluation.Metrics;

/// <summary>
/// Maps an <see cref="LlmJudgeResult"/> to a <see cref="MetricScore"/> for the judge-backed
/// metrics (<c>LlmJudgeMetric</c> and the RAG metric pack via <c>RagJudgeMetricBase</c>).
/// </summary>
/// <remarks>
/// Single owner of the result→score projection so a new judge-result field (e.g. the jury
/// <see cref="MetricScore.Consensus"/> / <see cref="MetricScore.Spread"/> fields) is a
/// one-line change here instead of a copy-paste edit that can drift between the two metric
/// families.
/// </remarks>
internal static class JudgeMetricScoreMapper
{
    /// <summary>
    /// Projects a judge result to a metric score: a parsed result passes/fails against the
    /// threshold (carrying any jury consensus); any other outcome soft-fails to
    /// <see cref="Verdict.Warn"/>.
    /// </summary>
    /// <param name="metricKey">The metric key to stamp on the score.</param>
    /// <param name="result">The judge (or jury) result.</param>
    /// <param name="threshold">The metric's pass threshold.</param>
    /// <param name="duration">How long the metric took to score the case.</param>
    public static MetricScore ToMetricScore(
        string metricKey,
        LlmJudgeResult result,
        double threshold,
        TimeSpan duration) => result.Outcome switch
    {
        LlmJudgeOutcome.Parsed => new MetricScore
        {
            MetricKey = metricKey,
            Score = result.Score,
            Verdict = result.Score >= threshold ? Verdict.Pass : Verdict.Fail,
            Reasoning = result.Reasoning,
            RawOutput = result.RawOutput,
            CostUsd = result.CostUsd,
            Duration = duration,
            // When a jury produced the score, surface its agreement on the dashboard.
            // Null for single-judge runs.
            Consensus = result.Panel?.Bucket,
            Spread = result.Panel?.Spread
        },
        _ => new MetricScore
        {
            MetricKey = metricKey,
            Score = 0.0,
            Verdict = Verdict.Warn,
            Reasoning = result.Reasoning,
            RawOutput = result.RawOutput,
            CostUsd = result.CostUsd,
            Duration = duration
        }
    };
}
