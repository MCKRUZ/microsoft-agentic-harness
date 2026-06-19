using Application.AI.Common.Evaluation;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Domain.AI.Evaluation;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Evaluation;

public sealed class JuryAggregatorTests
{
    private static PanelistVerdict Parsed(string name, double score) => new()
    {
        Name = name,
        Score = score,
        Outcome = LlmJudgeOutcome.Parsed
    };

    private static PanelistVerdict Excluded(string name, LlmJudgeOutcome outcome = LlmJudgeOutcome.Malformed) => new()
    {
        Name = name,
        Score = 0.0,
        Outcome = outcome
    };

    private static JuryAggregator.JuryAggregate Run(
        IReadOnlyList<PanelistVerdict> verdicts,
        JuryScoreAggregation aggregation = JuryScoreAggregation.Median,
        double consensusMaxSpread = 0.2,
        double conflictMinSpread = 0.5)
        => JuryAggregator.Aggregate(verdicts, aggregation, consensusMaxSpread, conflictMinSpread);

    [Fact]
    public void Median_of_odd_count_is_the_middle_value()
    {
        var result = Run(new[] { Parsed("a", 0.2), Parsed("b", 0.9), Parsed("c", 0.5) });

        result.Score.Should().Be(0.5);
    }

    [Fact]
    public void Median_of_even_count_averages_the_two_middle_values()
    {
        var result = Run(new[] { Parsed("a", 0.2), Parsed("b", 0.4), Parsed("c", 0.6), Parsed("d", 1.0) });

        result.Score.Should().Be(0.5); // (0.4 + 0.6) / 2
    }

    [Fact]
    public void Mean_averages_all_responders()
    {
        var result = Run(new[] { Parsed("a", 0.2), Parsed("b", 0.8) }, JuryScoreAggregation.Mean);

        result.Score.Should().Be(0.5);
    }

    [Fact]
    public void Min_takes_the_lowest_responder()
    {
        var result = Run(new[] { Parsed("a", 0.9), Parsed("b", 0.3), Parsed("c", 0.7) }, JuryScoreAggregation.Min);

        result.Score.Should().Be(0.3);
    }

    [Fact]
    public void Median_is_robust_to_a_single_outlier()
    {
        // Two judges agree at ~0.8, one outlier at 0.0 — median ignores the outlier.
        var result = Run(new[] { Parsed("a", 0.8), Parsed("b", 0.85), Parsed("c", 0.0) });

        result.Score.Should().Be(0.8);
    }

    [Fact]
    public void Tight_spread_is_bucketed_consensus()
    {
        var result = Run(new[] { Parsed("a", 0.80), Parsed("b", 0.85), Parsed("c", 0.90) });

        result.Panel.Bucket.Should().Be(ConsensusBucket.Consensus);
        result.Panel.Spread.Should().BeApproximately(0.10, 1e-9);
    }

    [Fact]
    public void Moderate_spread_is_bucketed_split()
    {
        var result = Run(new[] { Parsed("a", 0.5), Parsed("b", 0.8) }); // spread 0.3, between 0.2 and 0.5

        result.Panel.Bucket.Should().Be(ConsensusBucket.Split);
    }

    [Fact]
    public void Wide_spread_is_bucketed_conflict()
    {
        var result = Run(new[] { Parsed("a", 0.1), Parsed("b", 0.9) }); // spread 0.8 >= 0.5

        result.Panel.Bucket.Should().Be(ConsensusBucket.Conflict);
    }

    [Fact]
    public void Single_responder_has_zero_spread_and_consensus()
    {
        var result = Run(new[] { Parsed("solo", 0.42) });

        result.Score.Should().Be(0.42);
        result.Panel.Spread.Should().Be(0.0);
        result.Panel.Bucket.Should().Be(ConsensusBucket.Consensus);
        result.Panel.Responded.Should().Be(1);
        result.Panel.Excluded.Should().Be(0);
    }

    [Fact]
    public void Excluded_panelists_do_not_affect_the_score_but_are_counted()
    {
        var result = Run(new[] { Parsed("a", 0.7), Parsed("b", 0.7), Excluded("c") });

        result.Score.Should().Be(0.7);
        result.Panel.Responded.Should().Be(2);
        result.Panel.Excluded.Should().Be(1);
        result.Panel.Verdicts.Should().HaveCount(3);
    }

    [Fact]
    public void All_excluded_yields_zero_score_and_conflict_with_no_responders()
    {
        var result = Run(new[] { Excluded("a"), Excluded("b", LlmJudgeOutcome.InvocationFailed) });

        result.Score.Should().Be(0.0);
        result.Panel.Responded.Should().Be(0);
        result.Panel.Excluded.Should().Be(2);
        result.Panel.Bucket.Should().Be(ConsensusBucket.Conflict);
    }

    [Fact]
    public void Bucket_stays_deterministic_when_thresholds_are_misconfigured()
    {
        // consensusMaxSpread > conflictMinSpread: consensus is checked first, so a small
        // spread still resolves to Consensus rather than ambiguously to Conflict.
        var result = Run(
            new[] { Parsed("a", 0.50), Parsed("b", 0.55) },
            consensusMaxSpread: 0.6,
            conflictMinSpread: 0.3);

        result.Panel.Bucket.Should().Be(ConsensusBucket.Consensus);
    }
}
