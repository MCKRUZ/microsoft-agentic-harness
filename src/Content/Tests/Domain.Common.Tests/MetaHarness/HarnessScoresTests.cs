using Domain.Common.MetaHarness;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.MetaHarness;

/// <summary>
/// Tests for <see cref="HarnessScores"/> and <see cref="ExampleResult"/> records.
/// </summary>
public class HarnessScoresTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var scores = new HarnessScores();

        scores.PassRate.Should().Be(0.0);
        scores.TotalTokenCost.Should().Be(0);
        scores.PerExampleResults.Should().BeEmpty();
    }

    [Fact]
    public void Construction_WithAllProperties_SetsCorrectly()
    {
        var results = new[]
        {
            new ExampleResult { TaskId = "t1", Passed = true, TokenCost = 100 },
            new ExampleResult { TaskId = "t2", Passed = false, TokenCost = 200 }
        };
        var scoredAt = DateTimeOffset.UtcNow;

        var scores = new HarnessScores
        {
            PassRate = 0.5,
            TotalTokenCost = 300,
            PerExampleResults = results,
            ScoredAt = scoredAt
        };

        scores.PassRate.Should().Be(0.5);
        scores.TotalTokenCost.Should().Be(300);
        scores.PerExampleResults.Should().HaveCount(2);
        scores.ScoredAt.Should().Be(scoredAt);
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new HarnessScores { PassRate = 0.8 };

        var modified = original with { PassRate = 0.9 };

        original.PassRate.Should().Be(0.8);
        modified.PassRate.Should().Be(0.9);
    }
}

/// <summary>
/// Tests for <see cref="ExampleResult"/> record.
/// </summary>
public class ExampleResultTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var result = new ExampleResult();

        result.TaskId.Should().BeEmpty();
        result.Passed.Should().BeFalse();
        result.TokenCost.Should().Be(0);
    }

    [Fact]
    public void Construction_SetsProperties()
    {
        var result = new ExampleResult
        {
            TaskId = "task-01",
            Passed = true,
            TokenCost = 1500
        };

        result.TaskId.Should().Be("task-01");
        result.Passed.Should().BeTrue();
        result.TokenCost.Should().Be(1500);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new ExampleResult { TaskId = "t1", Passed = true, TokenCost = 100 };
        var b = new ExampleResult { TaskId = "t1", Passed = true, TokenCost = 100 };

        a.Should().Be(b);
    }
}
