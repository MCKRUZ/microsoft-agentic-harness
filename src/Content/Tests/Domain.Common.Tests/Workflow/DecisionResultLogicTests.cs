using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="DecisionResult"/> logic methods: IsGo, IsConditionalGo,
/// IsNoGo, CanProceed, GetScore, GetIssueCounts.
/// </summary>
public class DecisionResultLogicTests
{
    // ── IsGo ──

    [Fact]
    public void IsGo_GoOutcome_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "go" };

        result.IsGo().Should().BeTrue();
    }

    [Fact]
    public void IsGo_GoOutcomeCaseInsensitive_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "GO" };

        result.IsGo().Should().BeTrue();
    }

    [Fact]
    public void IsGo_NonGoOutcome_ReturnsFalse()
    {
        var result = new DecisionResult { Outcome = "no_go" };

        result.IsGo().Should().BeFalse();
    }

    // ── IsConditionalGo ──

    [Fact]
    public void IsConditionalGo_ConditionalGoOutcome_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "conditional_go" };

        result.IsConditionalGo().Should().BeTrue();
    }

    [Fact]
    public void IsConditionalGo_ConditionalOutcome_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "conditional" };

        result.IsConditionalGo().Should().BeTrue();
    }

    [Fact]
    public void IsConditionalGo_CaseInsensitive_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "CONDITIONAL_GO" };

        result.IsConditionalGo().Should().BeTrue();
    }

    // ── IsNoGo ──

    [Fact]
    public void IsNoGo_NoGoOutcome_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "no_go" };

        result.IsNoGo().Should().BeTrue();
    }

    [Fact]
    public void IsNoGo_CaseInsensitive_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "NO_GO" };

        result.IsNoGo().Should().BeTrue();
    }

    // ── CanProceed ──

    [Fact]
    public void CanProceed_GoOutcome_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "go" };

        result.CanProceed().Should().BeTrue();
    }

    [Fact]
    public void CanProceed_ConditionalGoOutcome_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "conditional_go" };

        result.CanProceed().Should().BeTrue();
    }

    [Fact]
    public void CanProceed_NoGoOutcome_ReturnsFalse()
    {
        var result = new DecisionResult { Outcome = "no_go" };

        result.CanProceed().Should().BeFalse();
    }

    // ── GetScore ──

    [Fact]
    public void GetScore_ScoreInMetadata_ReturnsScore()
    {
        var result = new DecisionResult
        {
            Outcome = "go",
            Metadata = new Dictionary<string, object> { ["score"] = 92 }
        };

        result.GetScore().Should().Be(92);
    }

    [Fact]
    public void GetScore_NoScoreInMetadata_ReturnsZero()
    {
        var result = new DecisionResult { Outcome = "go" };

        result.GetScore().Should().Be(0);
    }

    [Fact]
    public void GetScore_WrongTypeInMetadata_ReturnsZero()
    {
        var result = new DecisionResult
        {
            Outcome = "go",
            Metadata = new Dictionary<string, object> { ["score"] = "not-int" }
        };

        result.GetScore().Should().Be(0);
    }

    // ── GetIssueCounts ──

    [Fact]
    public void GetIssueCounts_AllPresent_ReturnsTuple()
    {
        var result = new DecisionResult
        {
            Outcome = "conditional_go",
            Metadata = new Dictionary<string, object>
            {
                ["critical_issues"] = 1,
                ["high_issues"] = 3,
                ["medium_issues"] = 5,
                ["low_issues"] = 10
            }
        };

        var (critical, high, medium, low) = result.GetIssueCounts();

        critical.Should().Be(1);
        high.Should().Be(3);
        medium.Should().Be(5);
        low.Should().Be(10);
    }

    [Fact]
    public void GetIssueCounts_NoMetadata_ReturnsAllZeros()
    {
        var result = new DecisionResult { Outcome = "go" };

        var (critical, high, medium, low) = result.GetIssueCounts();

        critical.Should().Be(0);
        high.Should().Be(0);
        medium.Should().Be(0);
        low.Should().Be(0);
    }

    [Fact]
    public void GetIssueCounts_PartialMetadata_ReturnsZerosForMissing()
    {
        var result = new DecisionResult
        {
            Outcome = "go",
            Metadata = new Dictionary<string, object> { ["critical_issues"] = 2 }
        };

        var (critical, high, medium, low) = result.GetIssueCounts();

        critical.Should().Be(2);
        high.Should().Be(0);
        medium.Should().Be(0);
        low.Should().Be(0);
    }

    [Fact]
    public void GetIssueCounts_WrongTypeInMetadata_ReturnsZeroForThat()
    {
        var result = new DecisionResult
        {
            Outcome = "go",
            Metadata = new Dictionary<string, object> { ["critical_issues"] = "not-int" }
        };

        var (critical, _, _, _) = result.GetIssueCounts();

        critical.Should().Be(0);
    }
}
