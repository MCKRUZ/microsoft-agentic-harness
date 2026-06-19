using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Evaluation;

/// <summary>
/// Pure, stateless reduction of a panel of judge verdicts to a single aggregate score
/// plus a consensus summary. No I/O — exhaustively unit-testable in isolation.
/// </summary>
/// <remarks>
/// Only <see cref="LlmJudgeOutcome.Parsed"/> panelists contribute to the aggregate score
/// and the spread; failed/malformed panelists are counted as excluded but never sway the
/// number (the "robust to one bad judge" property the jury exists for).
/// </remarks>
public static class JuryAggregator
{
    /// <summary>The aggregate score and the panel summary produced from a set of verdicts.</summary>
    /// <param name="Score">The reduced score in [0, 1] (median/mean/min of responders); <c>0.0</c> when none responded.</param>
    /// <param name="Panel">The consensus summary plus every panelist verdict (responders and excluded).</param>
    public readonly record struct JuryAggregate(double Score, JuryPanelResult Panel);

    /// <summary>
    /// Reduces <paramref name="verdicts"/> to an aggregate score and a
    /// <see cref="JuryPanelResult"/> using the supplied aggregation and spread thresholds.
    /// </summary>
    /// <param name="verdicts">All panelist verdicts (responders and excluded).</param>
    /// <param name="aggregation">How responder scores reduce to one number.</param>
    /// <param name="consensusMaxSpread">Spread at or below which the panel is <see cref="ConsensusBucket.Consensus"/>.</param>
    /// <param name="conflictMinSpread">Spread at or above which the panel is <see cref="ConsensusBucket.Conflict"/>.</param>
    /// <returns>The aggregate score and panel summary.</returns>
    public static JuryAggregate Aggregate(
        IReadOnlyList<PanelistVerdict> verdicts,
        JuryScoreAggregation aggregation,
        double consensusMaxSpread,
        double conflictMinSpread)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        var responderScores = verdicts
            .Where(v => v.Outcome == LlmJudgeOutcome.Parsed)
            .Select(v => v.Score)
            .ToArray();

        var responded = responderScores.Length;
        var excluded = verdicts.Count - responded;

        // No usable scores — caller (JuryLlmJudge) normally soft-fails before reaching
        // here; stay defensive and report max disagreement rather than a false 0-consensus.
        if (responded == 0)
        {
            return new JuryAggregate(
                0.0,
                new JuryPanelResult
                {
                    Verdicts = verdicts,
                    Bucket = ConsensusBucket.Conflict,
                    Spread = 0.0,
                    Responded = 0,
                    Excluded = excluded
                });
        }

        var score = Reduce(responderScores, aggregation);
        var spread = responderScores.Max() - responderScores.Min();
        var bucket = Bucketize(spread, consensusMaxSpread, conflictMinSpread);

        return new JuryAggregate(
            score,
            new JuryPanelResult
            {
                Verdicts = verdicts,
                Bucket = bucket,
                Spread = spread,
                Responded = responded,
                Excluded = excluded
            });
    }

    private static double Reduce(double[] scores, JuryScoreAggregation aggregation) => aggregation switch
    {
        JuryScoreAggregation.Mean => scores.Average(),
        JuryScoreAggregation.Min => scores.Min(),
        _ => Median(scores)
    };

    private static double Median(double[] scores)
    {
        var sorted = (double[])scores.Clone();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // Consensus is checked before conflict so the buckets stay deterministic even if a
    // consumer misconfigures the thresholds (consensusMaxSpread > conflictMinSpread): a
    // single responder has spread 0 and is always Consensus.
    private static ConsensusBucket Bucketize(double spread, double consensusMaxSpread, double conflictMinSpread)
    {
        if (spread <= consensusMaxSpread) return ConsensusBucket.Consensus;
        if (spread >= conflictMinSpread) return ConsensusBucket.Conflict;
        return ConsensusBucket.Split;
    }
}
