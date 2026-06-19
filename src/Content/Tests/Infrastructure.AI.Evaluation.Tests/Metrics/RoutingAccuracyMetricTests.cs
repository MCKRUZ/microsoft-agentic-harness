using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Metrics;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Metrics;

public sealed class RoutingAccuracyMetricTests
{
    private readonly RoutingAccuracyMetric _sut = new();

    [Fact]
    public void Key_IsRoutingAccuracy() => _sut.Key.Should().Be("routing_accuracy");

    [Fact]
    public async Task ScoreAsync_PredictedLabelEqualsExpected_ReturnsPass()
    {
        var score = await _sut.ScoreAsync(
            Case("MultiHop"),
            Output("MultiHop"),
            Spec(),
            CancellationToken.None);

        score.Score.Should().Be(1.0);
        score.Verdict.Should().Be(Verdict.Pass);
    }

    [Theory]
    [InlineData("multi_hop")]
    [InlineData("multihop")]
    [InlineData("MULTIHOP")]
    [InlineData(" Multi-Hop ")]
    public async Task ScoreAsync_NormalizesCaseAndSeparators_Matches(string goldLabel)
    {
        var score = await _sut.ScoreAsync(Case(goldLabel), Output("MultiHop"), Spec(), CancellationToken.None);

        score.Score.Should().Be(1.0);
        score.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public async Task ScoreAsync_PredictedDiffersFromExpected_ReturnsFail()
    {
        var score = await _sut.ScoreAsync(Case("SimpleLookup"), Output("MultiHop"), Spec(), CancellationToken.None);

        score.Score.Should().Be(0.0);
        score.Verdict.Should().Be(Verdict.Fail);
        score.Reasoning.Should().Contain("SimpleLookup");
    }

    [Fact]
    public async Task ScoreAsync_NoExpectedLabel_ReturnsWarn()
    {
        var score = await _sut.ScoreAsync(Case(null), Output("MultiHop"), Spec(), CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Warn);
        score.Reasoning.Should().Contain("expected_output");
    }

    [Fact]
    public async Task ScoreAsync_InvocationFailed_ReturnsWarnNotFail()
    {
        var failed = new AgentInvocationResult { Success = false, Output = string.Empty, Error = "probe exploded" };

        var score = await _sut.ScoreAsync(Case("MultiHop"), failed, Spec(), CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Warn);
        score.Reasoning.Should().Contain("probe exploded");
    }

    private static EvalCase Case(string? expected) => new()
    {
        Id = "c1",
        Input = "in",
        ExpectedOutput = expected,
        MetricSpecs = []
    };

    private static AgentInvocationResult Output(string label) => new() { Success = true, Output = label };

    private static MetricSpec Spec() => new() { MetricKey = "routing_accuracy" };
}
