using Domain.AI.Evaluation;

namespace Infrastructure.AI.Evaluation.Tests.Reporters;

internal static class TestReportFactory
{
    public static EvalRunReport DeterministicReport()
    {
        var startedAt = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);
        var duration = TimeSpan.FromMilliseconds(750);

        var passCase = new EvalCase
        {
            Id = "case-pass",
            Input = "Why is the sky blue?",
            ExpectedOutput = "Rayleigh scattering.",
            MetricSpecs = [new MetricSpec { MetricKey = "exact_match", Threshold = 1.0 }]
        };

        var failCase = new EvalCase
        {
            Id = "case-fail",
            Input = "Hello",
            ExpectedOutput = "Hi",
            MetricSpecs = [new MetricSpec { MetricKey = "exact_match", Threshold = 1.0 }]
        };

        var warnCase = new EvalCase
        {
            Id = "case-warn",
            Input = "Judge me",
            ExpectedOutput = null,
            MetricSpecs = [new MetricSpec { MetricKey = "llm_judge", Threshold = 0.7 }]
        };

        var dataset = new EvalDataset
        {
            Name = "demo-dataset",
            Version = "1.0.0",
            Description = "Deterministic fixture for reporter tests.",
            SourcePath = null,
            Cases = [passCase, failCase, warnCase]
        };

        var passScore = new MetricScore
        {
            MetricKey = "exact_match",
            Score = 1.0,
            Verdict = Verdict.Pass,
            Reasoning = "Output equals expected.",
            Duration = TimeSpan.FromMilliseconds(2)
        };

        var failScore = new MetricScore
        {
            MetricKey = "exact_match",
            Score = 0.0,
            Verdict = Verdict.Fail,
            Reasoning = "Output differs from expected.",
            Duration = TimeSpan.FromMilliseconds(3)
        };

        var warnScore = new MetricScore
        {
            MetricKey = "llm_judge",
            Score = 0.0,
            Verdict = Verdict.Warn,
            Reasoning = "llm_judge requires a 'rubric' parameter.",
            Duration = TimeSpan.FromMilliseconds(1)
        };

        var passResult = new EvalResult
        {
            Case = passCase,
            OutputPerRepeat = ["Rayleigh scattering."],
            ScoresPerRepeat = [new[] { passScore }],
            AggregatedScores = new SortedDictionary<string, MetricScore> { ["exact_match"] = passScore },
            Verdict = Verdict.Pass,
            Duration = TimeSpan.FromMilliseconds(50)
        };

        var failResult = new EvalResult
        {
            Case = failCase,
            OutputPerRepeat = ["Hello back"],
            ScoresPerRepeat = [new[] { failScore }],
            AggregatedScores = new SortedDictionary<string, MetricScore> { ["exact_match"] = failScore },
            Verdict = Verdict.Fail,
            Duration = TimeSpan.FromMilliseconds(40)
        };

        var warnResult = new EvalResult
        {
            Case = warnCase,
            OutputPerRepeat = ["whatever"],
            ScoresPerRepeat = [new[] { warnScore }],
            AggregatedScores = new SortedDictionary<string, MetricScore> { ["llm_judge"] = warnScore },
            Verdict = Verdict.Warn,
            Duration = TimeSpan.FromMilliseconds(30)
        };

        return new EvalRunReport
        {
            RunId = "run-fixture-001",
            StartedAtUtc = startedAt,
            CompletedAtUtc = startedAt + duration,
            Duration = duration,
            Datasets = [dataset],
            Results = [passResult, failResult, warnResult],
            PassedCount = 1,
            FailedCount = 1,
            WarnedCount = 1,
            ErroredCount = 0,
            TotalCostUsd = 0m,
            Repeats = 1,
            OverallVerdict = Verdict.Fail
        };
    }

    /// <summary>A one-metric result for a case with the given id and verdict; adjust it with <c>with</c>.</summary>
    public static EvalResult SingleResult(string caseId, Verdict verdict)
    {
        var score = new MetricScore { MetricKey = "exact_match", Score = verdict == Verdict.Pass ? 1.0 : 0.0, Verdict = verdict };
        return new EvalResult
        {
            Case = new EvalCase { Id = caseId, Input = "x", MetricSpecs = [new MetricSpec { MetricKey = "exact_match" }] },
            OutputPerRepeat = [string.Empty],
            ScoresPerRepeat = [new[] { score }],
            AggregatedScores = new Dictionary<string, MetricScore> { ["exact_match"] = score },
            Verdict = verdict
        };
    }

    /// <summary>A report holding exactly <paramref name="results"/>, in one dataset.</summary>
    public static EvalRunReport ReportOf(params EvalResult[] results) => new()
    {
        RunId = "run-single",
        StartedAtUtc = new DateTimeOffset(2026, 5, 29, 12, 0, 0, TimeSpan.Zero),
        CompletedAtUtc = new DateTimeOffset(2026, 5, 29, 12, 0, 1, TimeSpan.Zero),
        Duration = TimeSpan.FromSeconds(1),
        Datasets = [new EvalDataset { Name = "d", Cases = [.. results.Select(r => r.Case)] }],
        Results = results,
        FailedCount = results.Count(r => r.Verdict == Verdict.Fail),
        ErroredCount = results.Count(r => r.Error is not null),
        OverallVerdict = Verdict.Fail
    };
}
