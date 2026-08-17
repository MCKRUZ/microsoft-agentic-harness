using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Runners;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Runners;

public sealed class EvalRunnerTests
{
    [Fact]
    public async Task RunAsync_EmptyDatasets_ReturnsEmptyReportWithPassVerdict()
    {
        var sut = BuildSut(out _, out _);

        var report = await sut.RunAsync([], new EvalRunOptions(), CancellationToken.None);

        report.Results.Should().BeEmpty();
        report.OverallVerdict.Should().Be(Verdict.Pass);
        report.PassedCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_AllCasesPass_ReturnsPassReport()
    {
        var sut = BuildSut(out var invoker, out _);
        invoker.Setup(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "match" });

        var dataset = DatasetWith(
            Case("c1", expected: "match"),
            Case("c2", expected: "match"));

        var report = await sut.RunAsync([dataset], new EvalRunOptions(), CancellationToken.None);

        report.PassedCount.Should().Be(2);
        report.FailedCount.Should().Be(0);
        report.OverallVerdict.Should().Be(Verdict.Pass);
        report.PassRate.Should().Be(1.0);
    }

    [Fact]
    public async Task RunAsync_OneCaseFails_OverallFailWhenThresholdZero()
    {
        var sut = BuildSut(out var invoker, out _);
        invoker.SetupSequence(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "match" })
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "wrong" });

        var dataset = DatasetWith(
            Case("c1", expected: "match"),
            Case("c2", expected: "match"));

        var report = await sut.RunAsync([dataset], new EvalRunOptions { FailRateThreshold = 0.0 }, CancellationToken.None);

        report.PassedCount.Should().Be(1);
        report.FailedCount.Should().Be(1);
        report.OverallVerdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public async Task RunAsync_OneCaseFails_OverallPassWhenWithinThreshold()
    {
        var sut = BuildSut(out var invoker, out _);
        invoker.SetupSequence(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "match" })
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "match" })
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "wrong" });

        var dataset = DatasetWith(
            Case("c1", expected: "match"),
            Case("c2", expected: "match"),
            Case("c3", expected: "match"));

        var report = await sut.RunAsync([dataset], new EvalRunOptions { FailRateThreshold = 0.5 }, CancellationToken.None);

        report.PassedCount.Should().Be(2);
        report.FailedCount.Should().Be(1);
        report.OverallVerdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public async Task RunAsync_TagFilter_FiltersCasesNotMatching()
    {
        var sut = BuildSut(out var invoker, out _);
        invoker.Setup(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "match" });

        var dataset = DatasetWith(
            Case("c1", expected: "match", tags: ["pii"]),
            Case("c2", expected: "match", tags: ["smoke"]));

        var report = await sut.RunAsync(
            [dataset],
            new EvalRunOptions { TagFilter = ["pii"] },
            CancellationToken.None);

        report.Results.Should().HaveCount(1);
        report.Results[0].Case.Id.Should().Be("c1");
    }

    [Fact]
    public async Task RunAsync_Repeats_InvokesMultipleTimesAndAggregatesMedian()
    {
        var sut = BuildSut(out var invoker, out _);
        invoker.Setup(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "match" });

        var dataset = DatasetWith(Case("c1", expected: "match"));

        var report = await sut.RunAsync([dataset], new EvalRunOptions { Repeats = 3 }, CancellationToken.None);

        invoker.Verify(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        report.Results[0].OutputPerRepeat.Should().HaveCount(3);
        report.Results[0].ScoresPerRepeat.Should().HaveCount(3);
        report.Repeats.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_InvokerThrows_RecordsCaseAsErroredButContinues()
    {
        var sut = BuildSut(out var invoker, out _);
        invoker.SetupSequence(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("boom"))
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "match" });

        var dataset = DatasetWith(
            Case("c1", expected: "match"),
            Case("c2", expected: "match"));

        var report = await sut.RunAsync([dataset], new EvalRunOptions(), CancellationToken.None);

        report.ErroredCount.Should().Be(1);
        report.PassedCount.Should().Be(1);
        report.Results[0].Error.Should().Contain("boom");
    }

    [Fact]
    public async Task RunAsync_UnknownMetric_RecordsWarnNotError()
    {
        var sut = BuildSut(out var invoker, out _);
        invoker.Setup(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "match" });

        var caseWithBogusMetric = new EvalCase
        {
            Id = "c1",
            Input = "i",
            ExpectedOutput = "match",
            MetricSpecs = [new MetricSpec { MetricKey = "no_such_metric" }]
        };

        var dataset = new EvalDataset { Name = "d", Cases = [caseWithBogusMetric] };

        var report = await sut.RunAsync([dataset], new EvalRunOptions(), CancellationToken.None);

        report.WarnedCount.Should().Be(1);
        report.Results[0].AggregatedScores["no_such_metric"].Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    public async Task Median_aggregation_carries_the_violated_clause_from_the_representative_repeat()
    {
        // Scores [0.2, 0.5, 0.9] median to the middle sample exactly (distance 0) — the
        // representative must be that repeat specifically, not repeat 1 or a synthesized mix.
        var metric = new Mock<IEvalMetric>();
        metric.SetupGet(m => m.Key).Returns("judged");
        metric.SetupSequence(m => m.ScoreAsync(
                It.IsAny<EvalCase>(), It.IsAny<AgentInvocationResult>(), It.IsAny<MetricSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JudgedScore(0.2, "repeat-1-clause"))
            .ReturnsAsync(JudgedScore(0.5, "repeat-2-clause"))
            .ReturnsAsync(JudgedScore(0.9, "repeat-3-clause"));

        var invoker = new Mock<IAgentInvoker>(MockBehavior.Strict);
        invoker.Setup(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "out" });
        var sut = new EvalRunner(invoker.Object, [metric.Object], NullLogger<EvalRunner>.Instance);
        var dataset = DatasetWith(JudgedCase("c1"));

        var report = await sut.RunAsync([dataset], new EvalRunOptions { Repeats = 3 }, CancellationToken.None);

        report.Results[0].AggregatedScores["judged"].ViolatedClause.Should().Be("repeat-2-clause");
    }

    [Fact]
    public async Task Median_aggregation_no_longer_drops_consensus_and_spread()
    {
        var metric = new Mock<IEvalMetric>();
        metric.SetupGet(m => m.Key).Returns("judged");
        metric.Setup(m => m.ScoreAsync(
                It.IsAny<EvalCase>(), It.IsAny<AgentInvocationResult>(), It.IsAny<MetricSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JudgedScore(0.8, "clause", ConsensusBucket.Split, spread: 0.3));

        var invoker = new Mock<IAgentInvoker>(MockBehavior.Strict);
        invoker.Setup(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "out" });
        var sut = new EvalRunner(invoker.Object, [metric.Object], NullLogger<EvalRunner>.Instance);
        var dataset = DatasetWith(JudgedCase("c1"));

        var report = await sut.RunAsync([dataset], new EvalRunOptions { Repeats = 3 }, CancellationToken.None);

        var aggregated = report.Results[0].AggregatedScores["judged"];
        aggregated.Consensus.Should().Be(ConsensusBucket.Split);
        aggregated.Spread.Should().Be(0.3);
    }

    [Fact]
    public async Task Median_aggregation_reasoning_text_is_unchanged_for_multi_repeat()
    {
        // Pins the existing "Median of N repeats." summary text — the representative-repeat
        // fix must not disturb it.
        var metric = new Mock<IEvalMetric>();
        metric.SetupGet(m => m.Key).Returns("judged");
        metric.Setup(m => m.ScoreAsync(
                It.IsAny<EvalCase>(), It.IsAny<AgentInvocationResult>(), It.IsAny<MetricSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JudgedScore(0.8, "clause"));

        var invoker = new Mock<IAgentInvoker>(MockBehavior.Strict);
        invoker.Setup(i => i.InvokeAsync(It.IsAny<EvalCase>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentInvocationResult { Success = true, Output = "out" });
        var sut = new EvalRunner(invoker.Object, [metric.Object], NullLogger<EvalRunner>.Instance);
        var dataset = DatasetWith(JudgedCase("c1"));

        var report = await sut.RunAsync([dataset], new EvalRunOptions { Repeats = 3 }, CancellationToken.None);

        report.Results[0].AggregatedScores["judged"].Reasoning.Should().Be("Median of 3 repeats.");
    }

    private static MetricScore JudgedScore(
        double score, string violatedClause, ConsensusBucket? consensus = null, double? spread = null) => new()
    {
        MetricKey = "judged",
        Score = score,
        Verdict = Verdict.Pass,
        RawOutput = $"raw-{violatedClause}",
        ViolatedClause = violatedClause,
        Consensus = consensus,
        Spread = spread
    };

    private static EvalCase JudgedCase(string id) => new()
    {
        Id = id,
        Input = "in-" + id,
        MetricSpecs = [new MetricSpec { MetricKey = "judged", Threshold = 0.0 }]
    };

    private static EvalCase Case(string id, string expected, IReadOnlyList<string>? tags = null) => new()
    {
        Id = id,
        Input = "in-" + id,
        ExpectedOutput = expected,
        Tags = tags ?? [],
        MetricSpecs = [new MetricSpec { MetricKey = "exact_match" }]
    };

    private static EvalDataset DatasetWith(params EvalCase[] cases) => new()
    {
        Name = "test-dataset",
        Cases = cases
    };

    private static EvalRunner BuildSut(out Mock<IAgentInvoker> invoker, out IReadOnlyList<IEvalMetric> metrics)
    {
        invoker = new Mock<IAgentInvoker>(MockBehavior.Strict);
        metrics = [new Infrastructure.AI.Evaluation.Metrics.ExactMatchMetric()];
        return new EvalRunner(invoker.Object, metrics, NullLogger<EvalRunner>.Instance);
    }
}
