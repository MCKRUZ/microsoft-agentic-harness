using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Models.Conversations;
using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Evaluation.SkillTraining;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.SkillTraining;

/// <summary>
/// Pins <see cref="RolloutTrajectory"/> and <see cref="RolloutScoring"/> (#843): what the proposer is shown
/// of a run, and how a run becomes the loop's two numbers.
/// </summary>
public sealed class RolloutTrajectoryAndScoringTests
{
    private static EvalCase Case() => new()
    {
        Id = "c1",
        Input = "Reconcile invoice 42",
        ExpectedOutput = "SECRET-ANSWER-KEY",
        Tags = ["split:val", "secret-tag"],
        MetricSpecs = [new MetricSpec { MetricKey = "exact_match", Parameters = new Dictionary<string, string> { ["pattern"] = "SECRET-PATTERN" } }]
    };

    private static MetricScore Score(double score, Verdict verdict) =>
        new() { MetricKey = "m", Score = score, Verdict = verdict };

    [Fact]
    public void Render_ShowsTheTaskEveryToolCallInOrderAndTheFinalAnswer()
    {
        var invocation = new AgentInvocationResult
        {
            Success = true,
            Output = "Total is 90.",
            ToolCalls =
            [
                new ToolCallRecord("lookup", "{\"id\":42}", "{\"total\":90}", 5),
                new ToolCallRecord("calc", "{\"x\":1}", null, 2)
            ]
        };

        var text = RolloutTrajectory.Render(Case(), invocation);

        text.Should().Contain("Reconcile invoice 42");
        text.IndexOf("Step 1: lookup", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Step 2: calc", StringComparison.Ordinal));
        text.Should().Contain("{\"id\":42}").And.Contain("{\"total\":90}").And.Contain("(no result recorded)");
        text.Should().EndWith("# FINAL ANSWER\nTotal is 90.");
    }

    [Fact]
    public void Render_NeverContainsTheAnswerKeyTheMetricParametersOrTheTags()
    {
        var invocation = new AgentInvocationResult { Success = true, Output = "x" };

        var text = RolloutTrajectory.Render(Case(), invocation);

        text.Should().NotContain("SECRET-ANSWER-KEY").And.NotContain("SECRET-PATTERN").And.NotContain("secret-tag").And.NotContain("split:");
    }

    [Fact]
    public void Render_AFailedRun_SaysItDidNotFinishAndQuotesNoErrorText()
    {
        var invocation = new AgentInvocationResult
        {
            Success = false,
            Output = string.Empty,
            Error = "401 for https://store/blob?sig=SECRET-SAS-TOKEN"
        };

        var text = RolloutTrajectory.Render(Case(), invocation);

        text.Should().Contain("THE RUN DID NOT FINISH").And.NotContain("SECRET-SAS-TOKEN").And.NotContain("FINAL ANSWER");
    }

    [Fact]
    public void Render_WithOnlyToolNames_ListsThem()
    {
        var invocation = new AgentInvocationResult { Success = true, Output = "ok", ToolsInvoked = ["a", "b"] };

        RolloutTrajectory.Render(Case(), invocation).Should().Contain("a, b");
    }

    [Fact]
    public void Combine_EveryMetricPassing_IsHardOneAndSoftIsTheMean()
    {
        var (hard, soft) = RolloutScoring.Combine(true, [Score(1.0, Verdict.Pass), Score(0.5, Verdict.Pass)]);

        (hard, soft).Should().Be((1.0, 0.75));
    }

    [Fact]
    public void Combine_OneMetricFailing_IsHardZeroButKeepsTheGradedScore()
    {
        var (hard, soft) = RolloutScoring.Combine(true, [Score(1.0, Verdict.Pass), Score(0.4, Verdict.Fail)]);

        hard.Should().Be(0.0);
        soft.Should().BeApproximately(0.7, 1e-9);
    }

    [Fact]
    public void Combine_AMetricThatCouldNotDecide_IsNotAPass()
    {
        RolloutScoring.Combine(true, [Score(1.0, Verdict.Pass), Score(0.9, Verdict.Warn)]).Hard.Should().Be(0.0);
    }

    [Fact]
    public void Combine_ARunThatDidNotFinish_IsZeroOnBothWhateverTheMetricsSay()
    {
        RolloutScoring.Combine(false, [Score(1.0, Verdict.Pass)]).Should().Be((0.0, 0.0));
    }

    [Theory]
    [InlineData(1.7, 1.0)]
    [InlineData(-0.3, 0.0)]
    [InlineData(double.NaN, 0.0)]
    public void Combine_ASoftScoreOutsideTheRange_IsClamped(double raw, double expected)
    {
        RolloutScoring.Combine(true, [Score(raw, Verdict.Pass)]).Soft.Should().Be(expected);
    }
}
