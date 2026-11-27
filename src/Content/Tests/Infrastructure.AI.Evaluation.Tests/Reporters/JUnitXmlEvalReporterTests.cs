using System.Text;
using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Reporters;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Reporters;

public sealed class JUnitXmlEvalReporterTests
{
    [Fact]
    public void Key_IsJunit() => new JUnitXmlEvalReporter().FormatKey.Should().Be("junit");

    [Fact]
    public async Task Writes_deterministic_xml_matching_golden_fixture()
    {
        var report = TestReportFactory.DeterministicReport();
        var reporter = new JUnitXmlEvalReporter();

        using var ms = new MemoryStream();
        await reporter.WriteAsync(report, ms, CancellationToken.None);
        var actual = Encoding.UTF8.GetString(ms.ToArray());

        var actualPath = Path.Combine(AppContext.BaseDirectory, "actual-eval-report.junit.xml");
        await File.WriteAllTextAsync(actualPath, actual);

        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Reports", "expected-eval-report.junit.xml");
        File.Exists(goldenPath).Should().BeTrue($"golden fixture missing at {goldenPath}; actual written to {actualPath}");

        var expected = await File.ReadAllTextAsync(goldenPath);
        Normalize(actual).Should().Be(Normalize(expected));
    }

    [Fact]
    public async Task Tolerates_duplicate_case_ids_without_crashing()
    {
        // Two results with the same case id — should NOT throw ArgumentException.
        var c = new EvalCase
        {
            Id = "dup-id",
            Input = "x",
            MetricSpecs = [new MetricSpec { MetricKey = "exact_match" }]
        };
        var passScore = new MetricScore { MetricKey = "exact_match", Score = 1.0, Verdict = Verdict.Pass };
        var r1 = new EvalResult
        {
            Case = c, OutputPerRepeat = ["x"], ScoresPerRepeat = [new[] { passScore }],
            AggregatedScores = new Dictionary<string, MetricScore> { ["exact_match"] = passScore },
            Verdict = Verdict.Pass
        };
        var r2 = r1; // identical id, separate result instance is fine — same id is the test

        var report = new EvalRunReport
        {
            RunId = "dup-test",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Duration = TimeSpan.Zero,
            Datasets = [new EvalDataset { Name = "d", Cases = [c] }],
            Results = [r1, r2],
            PassedCount = 2,
            OverallVerdict = Verdict.Pass
        };

        var reporter = new JUnitXmlEvalReporter();
        using var ms = new MemoryStream();

        Func<Task> act = () => reporter.WriteAsync(report, ms, CancellationToken.None);
        await act.Should().NotThrowAsync();

        var xml = Encoding.UTF8.GetString(ms.ToArray());
        xml.Should().Contain("dup-id");
    }

    [Fact]
    public async Task Emits_skipped_for_cases_declared_but_not_executed()
    {
        var ranCase = new EvalCase { Id = "ran", Input = "x", MetricSpecs = [new MetricSpec { MetricKey = "exact_match" }] };
        var notRanCase = new EvalCase { Id = "not-ran", Input = "y", MetricSpecs = [new MetricSpec { MetricKey = "exact_match" }] };

        var passScore = new MetricScore { MetricKey = "exact_match", Score = 1.0, Verdict = Verdict.Pass };
        var ranResult = new EvalResult
        {
            Case = ranCase,
            OutputPerRepeat = ["x"],
            ScoresPerRepeat = [new[] { passScore }],
            AggregatedScores = new Dictionary<string, MetricScore> { ["exact_match"] = passScore },
            Verdict = Verdict.Pass
        };

        var report = new EvalRunReport
        {
            RunId = "partial-run",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Duration = TimeSpan.Zero,
            Datasets = [new EvalDataset { Name = "d", Cases = [ranCase, notRanCase] }],
            Results = [ranResult],  // only one of two cases has a result
            PassedCount = 1,
            OverallVerdict = Verdict.Pass
        };

        var reporter = new JUnitXmlEvalReporter();
        using var ms = new MemoryStream();
        await reporter.WriteAsync(report, ms, CancellationToken.None);
        var xml = Encoding.UTF8.GetString(ms.ToArray());

        xml.Should().Contain("name=\"ran\"");
        xml.Should().Contain("name=\"not-ran\"");
        xml.Should().Contain("not executed");
        // Suite reports 2 tests (both cases), not 1 (only executed).
        xml.Should().Contain("tests=\"2\"");
    }

    [Fact]
    public async Task Emits_passed_as_property_child_not_root_attribute()
    {
        var report = TestReportFactory.DeterministicReport();
        var reporter = new JUnitXmlEvalReporter();

        using var ms = new MemoryStream();
        await reporter.WriteAsync(report, ms, CancellationToken.None);
        var xml = Encoding.UTF8.GetString(ms.ToArray());

        // Non-standard `passed="..."` root attribute MUST NOT appear; XSD-legal
        // <property name="passed"> child MUST appear.
        xml.Should().NotContain(" passed=\"");
        xml.Should().Contain("<property name=\"passed\"");
    }

    [Fact]
    public async Task A_failure_whose_invocation_failed_carries_the_reason_in_its_message()
    {
        // A refused turn is scored, so it is a <failure>, not an <error>; the reason belongs in its message.
        var refused = TestReportFactory.SingleResult("refused", Verdict.Fail) with
        {
            InvocationFailurePerRepeat = ["Blocked: violence."]
        };

        using var ms = new MemoryStream();
        await new JUnitXmlEvalReporter().WriteAsync(TestReportFactory.ReportOf(refused), ms, CancellationToken.None);
        var xml = Encoding.UTF8.GetString(ms.ToArray());

        xml.Should().Contain("<failure message=\"exact_match=0.000(Fail); invocation failed: Blocked: violence.\"");
    }

    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}
