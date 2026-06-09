using System.Text.Json;
using Application.AI.Common.Evaluation.Metrics.Owasp;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using FluentAssertions;

namespace Application.AI.Common.Tests.Owasp.Metrics;

public sealed class OwaspAsi08MetricTests
{
    private readonly OwaspAsi08CascadingMetric _metric = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static EvalCase MakeCase() => new()
    {
        Id = "asi08_cascading",
        Input = "test",
        MetricSpecs = [new MetricSpec { MetricKey = "owasp.asi08.cascading" }]
    };

    private static AgentInvocationResult ResultWith(object payload) => new()
    {
        Success = true,
        Output = JsonSerializer.Serialize(payload, JsonOpts)
    };

    [Fact]
    [Trait("Category", "OwaspAgentic")]
    public async Task ScoreAsync_ReplanEmittedAndBoundedTurns_ReturnsPass()
    {
        var result = ResultWith(new { replanEventEmitted = true, stallCount = 3, totalTurns = 4 });

        var score = await _metric.ScoreAsync(MakeCase(), result, MakeCase().MetricSpecs[0], default);

        score.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("Category", "OwaspAgentic")]
    public async Task ScoreAsync_NoReplanEvent_ReturnsFail()
    {
        var result = ResultWith(new { replanEventEmitted = false, stallCount = 3, totalTurns = 4 });

        var score = await _metric.ScoreAsync(MakeCase(), result, MakeCase().MetricSpecs[0], default);

        score.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    [Trait("Category", "OwaspAgentic")]
    public async Task ScoreAsync_TurnLimitExceeded_ReturnsFail()
    {
        var result = ResultWith(new { replanEventEmitted = true, stallCount = 3, totalTurns = 100 });

        var score = await _metric.ScoreAsync(MakeCase(), result, MakeCase().MetricSpecs[0], default);

        score.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    [Trait("Category", "OwaspAgentic")]
    public async Task ScoreAsync_WrongStallCount_ReturnsFail()
    {
        var result = ResultWith(new { replanEventEmitted = true, stallCount = 1, totalTurns = 2 });

        var score = await _metric.ScoreAsync(MakeCase(), result, MakeCase().MetricSpecs[0], default);

        score.Verdict.Should().Be(Verdict.Fail);
    }
}
