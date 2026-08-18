using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Metrics;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Metrics;

public sealed class RegexMatchMetricTests
{
    private readonly RegexMatchMetric _sut = new();

    [Fact]
    public void Key_IsRegexMatch() => _sut.Key.Should().Be("regex_match");

    [Fact]
    public async Task ScoreAsync_PatternMatches_ReturnsPass()
    {
        var spec = Spec(@"^hello, \w+!$");
        var score = await _sut.ScoreAsync(Case(), Output("hello, world!"), spec, CancellationToken.None);

        score.Score.Should().Be(1.0);
        score.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public async Task ScoreAsync_PatternDoesNotMatch_ReturnsFail()
    {
        var spec = Spec(@"^\d+$");
        var score = await _sut.ScoreAsync(Case(), Output("not digits"), spec, CancellationToken.None);

        score.Score.Should().Be(0.0);
        score.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public async Task ScoreAsync_MissingPattern_ReturnsWarn()
    {
        var score = await _sut.ScoreAsync(Case(), Output("any"), Spec(pattern: null), CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Warn);
        score.Reasoning.Should().Contain("pattern");
    }

    [Fact]
    public async Task ScoreAsync_InvalidPattern_ReturnsWarn()
    {
        var score = await _sut.ScoreAsync(Case(), Output("any"), Spec(@"[unclosed"), CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    public async Task ScoreAsync_MustNotMatch_PatternAbsent_ReturnsPass()
    {
        var spec = Spec(@"Bearer\s+\w+", mustNotMatch: true);
        var score = await _sut.ScoreAsync(Case(), Output("no secrets here"), spec, CancellationToken.None);

        score.Score.Should().Be(1.0);
        score.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public async Task ScoreAsync_MustNotMatch_PatternPresent_ReturnsFail()
    {
        var spec = Spec(@"Bearer\s+\w+", mustNotMatch: true);
        var score = await _sut.ScoreAsync(Case(), Output("token: Bearer abc123"), spec, CancellationToken.None);

        score.Score.Should().Be(0.0);
        score.Verdict.Should().Be(Verdict.Fail);
    }

    private static EvalCase Case() => new() { Id = "c1", Input = "in", MetricSpecs = [] };
    private static AgentInvocationResult Output(string text) => new() { Success = true, Output = text };

    private static MetricSpec Spec(string? pattern = "match", bool? mustNotMatch = null)
    {
        var parameters = new Dictionary<string, string>();
        if (pattern is not null) parameters["pattern"] = pattern;
        if (mustNotMatch is not null) parameters["must_not_match"] = mustNotMatch.Value.ToString();
        return new MetricSpec { MetricKey = "regex_match", Parameters = parameters };
    }
}
