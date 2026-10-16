using Domain.AI.Evaluation;

namespace Infrastructure.AI.Evaluation.SkillTraining;

/// <summary>
/// Turns one case's metric scores into the loop's two rollout numbers (#843).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hard</b> is 1 only when the run finished and every metric passed, otherwise 0. A metric that
/// could not give a confident answer (<see cref="Verdict.Warn"/>) does not pass: a candidate is not
/// credited for a check that did not run.
/// </para>
/// <para>
/// <b>Soft</b> is the mean of the metric scores, clamped to [0, 1]. A run that did not finish scores 0
/// on both.
/// </para>
/// </remarks>
internal static class RolloutScoring
{
    /// <summary>Combines <paramref name="scores"/> for a run that did or did not finish.</summary>
    /// <param name="delivered">Whether the invocation finished and produced an answer.</param>
    /// <param name="scores">One score per metric spec of the case.</param>
    public static (double Hard, double Soft) Combine(bool delivered, IReadOnlyList<MetricScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        if (!delivered || scores.Count == 0)
            return (0.0, 0.0);

        var hard = scores.All(s => s.Verdict == Verdict.Pass) ? 1.0 : 0.0;
        var mean = scores.Average(s => double.IsNaN(s.Score) ? 0.0 : s.Score);
        return (hard, Math.Clamp(mean, 0.0, 1.0));
    }
}
