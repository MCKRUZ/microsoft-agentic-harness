using FluentAssertions;
using Presentation.EvalRunner.HarmonicWriteEval;
using Xunit;

namespace Presentation.EvalRunner.Tests.HarmonicWriteEval;

public sealed class HarmonicWriteMetricsTests
{
    private static FactAbstraction FA(string abstraction, string gold) =>
        new() { Abstraction = abstraction, GoldTopic = gold };

    [Fact]
    public void DistinctAbstractions_IsCaseInsensitive()
    {
        var assignments = new[] { FA("Topic A", "g1"), FA("topic a", "g1"), FA("Topic B", "g2") };

        HarmonicWriteMetrics.DistinctAbstractions(assignments).Should().Be(2);
    }

    [Theory]
    [InlineData(20, 5, 4.0)]
    [InlineData(5, 5, 1.0)]
    [InlineData(10, 4, 2.5)]
    public void FragmentationRatio_DividesDistinctByGold(int distinct, int gold, double expected)
    {
        HarmonicWriteMetrics.FragmentationRatio(distinct, gold).Should().Be(expected);
    }

    [Fact]
    public void FragmentationRatio_NoGoldTopics_IsZero()
    {
        HarmonicWriteMetrics.FragmentationRatio(3, 0).Should().Be(0);
    }

    [Fact]
    public void ClusterPurity_AllGroupsSingleTopic_IsOne()
    {
        var assignments = new[] { FA("A", "g1"), FA("A", "g1"), FA("B", "g2") };

        HarmonicWriteMetrics.ClusterPurity(assignments).Should().Be(1.0);
    }

    [Fact]
    public void ClusterPurity_MixedGroup_IsPenalized()
    {
        // Group A mixes g1 + g2 (dominant = 1 of 2); group B pure (1). Dominant total 2 of 3 facts.
        var assignments = new[] { FA("A", "g1"), FA("A", "g2"), FA("B", "g2") };

        HarmonicWriteMetrics.ClusterPurity(assignments).Should().BeApproximately(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public void ClusterPurity_Empty_IsZero()
    {
        HarmonicWriteMetrics.ClusterPurity([]).Should().Be(0);
    }
}
