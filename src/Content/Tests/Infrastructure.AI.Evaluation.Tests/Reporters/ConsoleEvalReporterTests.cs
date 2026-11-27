using System.Text;
using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Reporters;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Reporters;

public sealed class ConsoleEvalReporterTests
{
    [Fact]
    public void Key_IsConsole() => new ConsoleEvalReporter().FormatKey.Should().Be("console");

    [Fact]
    public async Task Writes_summary_with_counts_and_each_case_id()
    {
        var report = TestReportFactory.DeterministicReport();
        var reporter = new ConsoleEvalReporter();

        using var ms = new MemoryStream();
        await reporter.WriteAsync(report, ms, CancellationToken.None);
        var text = Encoding.UTF8.GetString(ms.ToArray());

        text.Should().Contain("run-fixture-001");
        text.Should().Contain("Pass: 1");
        text.Should().Contain("Fail: 1");
        text.Should().Contain("Warn: 1");
        text.Should().Contain("case-pass");
        text.Should().Contain("case-fail");
        text.Should().Contain("case-warn");
        text.Should().Contain("Overall:");
        text.Should().Contain("Cost:");
    }

    [Fact]
    public async Task Renders_case_id_containing_spectre_markup_chars_without_crashing()
    {
        // Build a minimal report whose case id includes '[' and ']' — Spectre's
        // markup parser would throw on these unless every interpolated cell is escaped.
        var c = new EvalCase
        {
            Id = "rag[smoke]",
            Input = "x",
            MetricSpecs = [new MetricSpec { MetricKey = "exact_match" }]
        };
        var score = new MetricScore { MetricKey = "exact_match", Score = 1.0, Verdict = Verdict.Pass };
        var result = new EvalResult
        {
            Case = c,
            OutputPerRepeat = ["x"],
            ScoresPerRepeat = [new[] { score }],
            AggregatedScores = new Dictionary<string, MetricScore> { ["exact_match"] = score },
            Verdict = Verdict.Pass
        };
        var report = new EvalRunReport
        {
            RunId = "run-[bracket]-1",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Duration = TimeSpan.Zero,
            Datasets = [new EvalDataset { Name = "d", Cases = [c] }],
            Results = [result],
            PassedCount = 1,
            OverallVerdict = Verdict.Pass
        };

        var reporter = new ConsoleEvalReporter();
        using var ms = new MemoryStream();

        Func<Task> act = () => reporter.WriteAsync(report, ms, CancellationToken.None);
        await act.Should().NotThrowAsync();

        var text = Encoding.UTF8.GetString(ms.ToArray());
        text.Should().Contain("rag[smoke]");
    }

    [Fact]
    public async Task Shows_why_a_case_failed_or_errored()
    {
        // Before, the table had no column for either: an errored case and a refused one showed only their verdicts.
        var refused = TestReportFactory.SingleResult("refused", Verdict.Fail) with
        {
            InvocationFailurePerRepeat = ["Blocked: [violence]."]
        };
        var errored = TestReportFactory.SingleResult("errored", Verdict.Fail) with
        {
            Error = "Skill 'default' not found."
        };

        var text = await RenderAsync(TestReportFactory.ReportOf(refused, errored));

        text.Should().Contain("Blocked: [violence].", "markup characters in a reason must be escaped, not parsed");
        text.Should().Contain("Skill 'default' not found.");
    }

    [Fact]
    public async Task Shortens_a_long_reason_to_one_line()
    {
        // A refusal body can run to several kilobytes over many lines; the table keeps one readable line.
        var longReason = "first line\n" + new string('x', 2000);
        var refused = TestReportFactory.SingleResult("refused", Verdict.Fail) with { InvocationFailurePerRepeat = [longReason] };

        var text = await RenderAsync(TestReportFactory.ReportOf(refused));

        text.Should().Contain("first line").And.NotContain(new string('x', 500));
    }

    private static async Task<string> RenderAsync(EvalRunReport report)
    {
        using var ms = new MemoryStream();
        await new ConsoleEvalReporter().WriteAsync(report, ms, CancellationToken.None);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
