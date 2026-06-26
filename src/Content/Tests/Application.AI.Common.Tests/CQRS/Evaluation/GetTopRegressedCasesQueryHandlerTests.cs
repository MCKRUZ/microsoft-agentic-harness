using Application.AI.Common.CQRS.Evaluation.GetTopRegressedCases;
using Application.AI.Common.Evaluation.Interfaces;
using Domain.AI.Evaluation;
using Domain.Common;
using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Evaluation;

public sealed class GetTopRegressedCasesQueryHandlerTests
{
    private readonly Mock<IEvalRunStore> _store = new();
    private readonly GetTopRegressedCasesQueryHandler _sut;

    public GetTopRegressedCasesQueryHandlerTests()
    {
        _sut = new GetTopRegressedCasesQueryHandler(
            _store.Object, NullLogger<GetTopRegressedCasesQueryHandler>.Instance);
    }

    private static EvalCase Case(string id, IReadOnlyList<string>? tags = null) => new()
    {
        Id = id,
        Input = "in",
        MetricSpecs = [new MetricSpec { MetricKey = "m1" }],
        Tags = tags ?? [],
    };

    private static MetricScore Score(double v) => new()
    {
        MetricKey = "m1",
        Score = v,
        Verdict = v >= 0.7 ? Verdict.Pass : Verdict.Fail,
    };

    private static EvalRunReport Report(
        string runId,
        params (string CaseId, double Score)[] cases)
    {
        var dataset = new EvalDataset
        {
            Name = "demo",
            Version = "1.0",
            Cases = cases.Select(c => Case(c.CaseId)).ToList(),
        };
        var results = cases.Select(c => new EvalResult
        {
            Case = Case(c.CaseId),
            OutputPerRepeat = [],
            ScoresPerRepeat = [],
            AggregatedScores = new Dictionary<string, MetricScore> { ["m1"] = Score(c.Score) },
            Verdict = Verdict.Pass,
        }).ToList();

        return new EvalRunReport
        {
            RunId = runId,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Duration = TimeSpan.Zero,
            Datasets = [dataset],
            Results = results,
            OverallVerdict = Verdict.Pass,
            Repeats = 1,
        };
    }

    [Fact]
    public async Task Returns_regressions_ordered_by_most_negative_delta()
    {
        _store.Setup(s => s.GetRunDetailAsync("baseline", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Report("baseline",
                  ("case-a", 0.9), ("case-b", 0.8), ("case-c", 0.5)));
        _store.Setup(s => s.GetRunDetailAsync("current", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Report("current",
                  ("case-a", 0.4), // delta -0.5
                  ("case-b", 0.7), // delta -0.1
                  ("case-c", 0.6))); // delta +0.1 (improvement — skip)

        var result = await _sut.Handle(new GetTopRegressedCasesQuery
        {
            CurrentRunId = "current",
            BaselineRunId = "baseline",
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].CaseId.Should().Be("case-a");
        result.Value[0].DatasetName.Should().Be("demo");
        result.Value[0].Delta.Should().BeApproximately(-0.5, 0.0001);
        result.Value[1].CaseId.Should().Be("case-b");
        result.Value[1].Delta.Should().BeApproximately(-0.1, 0.0001);
    }

    [Fact]
    public async Task Skips_cases_only_in_current_run()
    {
        _store.Setup(s => s.GetRunDetailAsync("baseline", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Report("baseline", ("case-a", 0.9)));
        _store.Setup(s => s.GetRunDetailAsync("current", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Report("current", ("case-a", 0.5), ("case-new", 0.1)));

        var result = await _sut.Handle(new GetTopRegressedCasesQuery
        {
            CurrentRunId = "current",
            BaselineRunId = "baseline",
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.CaseId == "case-a");
    }

    [Fact]
    public async Task Returns_NotFound_when_baseline_missing()
    {
        _store.Setup(s => s.GetRunDetailAsync("baseline", It.IsAny<CancellationToken>()))
              .ReturnsAsync((EvalRunReport?)null);
        _store.Setup(s => s.GetRunDetailAsync("current", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Report("current", ("case-a", 0.9)));

        var result = await _sut.Handle(new GetTopRegressedCasesQuery
        {
            CurrentRunId = "current",
            BaselineRunId = "baseline",
        }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
        result.Errors[0].Should().Contain("baseline");
    }

    [Fact]
    public async Task Respects_Take_limit()
    {
        _store.Setup(s => s.GetRunDetailAsync("baseline", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Report("baseline",
                  ("c1", 0.9), ("c2", 0.9), ("c3", 0.9)));
        _store.Setup(s => s.GetRunDetailAsync("current", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Report("current",
                  ("c1", 0.1), ("c2", 0.2), ("c3", 0.3)));

        var result = await _sut.Handle(new GetTopRegressedCasesQuery
        {
            CurrentRunId = "current",
            BaselineRunId = "baseline",
            Take = 2,
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Select(r => r.CaseId).Should().Equal("c1", "c2");
    }
}

public sealed class GetTopRegressedCasesQueryValidatorTests
{
    private readonly GetTopRegressedCasesQueryValidator _sut = new();

    private static GetTopRegressedCasesQuery Valid() => new()
    {
        CurrentRunId = "current",
        BaselineRunId = "baseline",
    };

    [Fact]
    public void Valid_query_has_no_errors()
        => _sut.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Empty_CurrentRunId_fails()
        => _sut.TestValidate(Valid() with { CurrentRunId = "" })
            .ShouldHaveValidationErrorFor(q => q.CurrentRunId);

    [Fact]
    public void Empty_BaselineRunId_fails()
        => _sut.TestValidate(Valid() with { BaselineRunId = "" })
            .ShouldHaveValidationErrorFor(q => q.BaselineRunId);

    [Fact]
    public void Same_run_ids_fails()
        => _sut.TestValidate(Valid() with { CurrentRunId = "x", BaselineRunId = "x" })
            .IsValid.Should().BeFalse();

    [Fact]
    public void Zero_Take_fails()
        => _sut.TestValidate(Valid() with { Take = 0 })
            .ShouldHaveValidationErrorFor(q => q.Take);

    [Fact]
    public void Over_max_Take_fails()
        => _sut.TestValidate(Valid() with { Take = GetTopRegressedCasesQueryValidator.MaxTake + 1 })
            .ShouldHaveValidationErrorFor(q => q.Take);
}
