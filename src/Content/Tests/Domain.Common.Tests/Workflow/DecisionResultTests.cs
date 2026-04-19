using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="DecisionResult"/> — outcome checks, score extraction,
/// issue counts, and defaults.
/// </summary>
public class DecisionResultTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var result = new DecisionResult();

        result.Outcome.Should().BeEmpty();
        result.Reason.Should().BeNull();
        result.Conditions.Should().BeEmpty();
        result.Metadata.Should().BeEmpty();
        result.MatchedRule.Should().BeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void IsGo_WithGoOutcome_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "go" };

        result.IsGo().Should().BeTrue();
    }

    [Fact]
    public void IsGo_CaseInsensitive()
    {
        var result = new DecisionResult { Outcome = "GO" };

        result.IsGo().Should().BeTrue();
    }

    [Fact]
    public void IsGo_WithOtherOutcome_ReturnsFalse()
    {
        var result = new DecisionResult { Outcome = "no_go" };

        result.IsGo().Should().BeFalse();
    }

    [Fact]
    public void IsConditionalGo_WithConditionalGo_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "conditional_go" };

        result.IsConditionalGo().Should().BeTrue();
    }

    [Fact]
    public void IsConditionalGo_WithConditional_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "conditional" };

        result.IsConditionalGo().Should().BeTrue();
    }

    [Fact]
    public void IsConditionalGo_CaseInsensitive()
    {
        var result = new DecisionResult { Outcome = "CONDITIONAL_GO" };

        result.IsConditionalGo().Should().BeTrue();
    }

    [Fact]
    public void IsNoGo_WithNoGoOutcome_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "no_go" };

        result.IsNoGo().Should().BeTrue();
    }

    [Fact]
    public void IsNoGo_CaseInsensitive()
    {
        var result = new DecisionResult { Outcome = "NO_GO" };

        result.IsNoGo().Should().BeTrue();
    }

    [Fact]
    public void CanProceed_WithGo_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "go" };

        result.CanProceed().Should().BeTrue();
    }

    [Fact]
    public void CanProceed_WithConditionalGo_ReturnsTrue()
    {
        var result = new DecisionResult { Outcome = "conditional_go" };

        result.CanProceed().Should().BeTrue();
    }

    [Fact]
    public void CanProceed_WithNoGo_ReturnsFalse()
    {
        var result = new DecisionResult { Outcome = "no_go" };

        result.CanProceed().Should().BeFalse();
    }

    [Fact]
    public void GetScore_WithScoreInMetadata_ReturnsValue()
    {
        var result = new DecisionResult
        {
            Metadata = new Dictionary<string, object> { ["score"] = 92 }
        };

        result.GetScore().Should().Be(92);
    }

    [Fact]
    public void GetScore_WithNoScore_ReturnsZero()
    {
        var result = new DecisionResult();

        result.GetScore().Should().Be(0);
    }

    [Fact]
    public void GetScore_WithNonIntScore_ReturnsZero()
    {
        var result = new DecisionResult
        {
            Metadata = new Dictionary<string, object> { ["score"] = "high" }
        };

        result.GetScore().Should().Be(0);
    }

    [Fact]
    public void GetIssueCounts_WithAllCounts_ReturnsTuple()
    {
        var result = new DecisionResult
        {
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
    public void GetIssueCounts_WithNoCounts_ReturnsZeros()
    {
        var result = new DecisionResult();

        var (critical, high, medium, low) = result.GetIssueCounts();

        critical.Should().Be(0);
        high.Should().Be(0);
        medium.Should().Be(0);
        low.Should().Be(0);
    }

    [Fact]
    public void EvaluatedAt_IsSetToUtcNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = new DecisionResult();

        result.EvaluatedAt.Should().BeAfter(before);
        result.EvaluatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }
}
