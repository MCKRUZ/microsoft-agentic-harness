using System.Globalization;
using Application.AI.Common.Evaluation.Interfaces;
using Domain.AI.Evaluation;
using Spectre.Console;

namespace Infrastructure.AI.Evaluation.Reporters;

/// <summary>
/// Renders an <see cref="EvalRunReport"/> to a human-readable table via
/// <see cref="Spectre.Console"/>. Intended for terminal output during local runs and CI logs.
/// </summary>
/// <remarks>
/// <para>
/// Writes via a Spectre <see cref="IAnsiConsole"/> that targets the supplied stream.
/// ANSI escape sequences are suppressed in the default configuration so the output
/// renders cleanly in both ANSI-capable and plain log sinks.
/// </para>
/// <para>
/// All user-influenced strings (case ids, verdict text, run id) are passed through
/// <see cref="Spectre.Console.Markup.Escape(string)"/> before rendering, so values
/// containing Spectre markup characters (<c>[</c>, <c>]</c>) do not crash the reporter.
/// </para>
/// <para>
/// Under the table, a "Reasons" list gives one line per case that errored (<see cref="EvalResult.Error"/>) or whose
/// invocation failed while still being scored (<see cref="EvalResult.InvocationFailurePerRepeat"/>), so a fail never
/// appears without its cause.
/// </para>
/// </remarks>
public sealed class ConsoleEvalReporter : IEvalReporter
{
    /// <inheritdoc />
    public string FormatKey => "console";

    /// <inheritdoc />
    public async Task WriteAsync(
        EvalRunReport report,
        Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        await using var writer = new StreamWriter(output, leaveOpen: true);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer)
        });

        var runIdSafe = Markup.Escape(report.RunId);
        var verdictSafe = Markup.Escape(report.OverallVerdict.ToString());

        console.MarkupLine($"[bold]Eval Run[/] {runIdSafe}");
        console.MarkupLine(
            $"Started: {report.StartedAtUtc:O}  Duration: {report.Duration.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}s  Repeats: {report.Repeats}");
        console.MarkupLine(
            $"Pass: {report.PassedCount}  Fail: {report.FailedCount}  Warn: {report.WarnedCount}  Error: {report.ErroredCount}  PassRate: {report.PassRate.ToString("P1", CultureInfo.InvariantCulture)}");
        console.MarkupLine(
            $"Cost: ${report.TotalCostUsd.ToString("F4", CultureInfo.InvariantCulture)}");
        console.MarkupLine($"Overall: [bold]{verdictSafe}[/]");
        console.WriteLine();

        var table = new Table();
        table.AddColumn("Case");
        table.AddColumn("Verdict");
        table.AddColumn("Score");
        table.AddColumn("Duration");

        foreach (var result in report.Results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double sum = 0.0;
            int count = 0;
            foreach (var s in result.AggregatedScores.Values)
            {
                sum += s.Score;
                count++;
            }
            var score = count == 0
                ? "—"
                : (sum / count).ToString("F2", CultureInfo.InvariantCulture);

            table.AddRow(
                Markup.Escape(result.Case.Id),
                Markup.Escape(result.Verdict.ToString()),
                Markup.Escape(score),
                Markup.Escape($"{result.Duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms"));
        }

        console.Write(table);
        WriteReasons(console, report.Results);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // Under the table rather than in a column: a reason is free text that a narrow table column would wrap into an
    // unreadable strip. One line per case, so a multi-kilobyte refusal body cannot flood the terminal.
    private static void WriteReasons(IAnsiConsole console, IReadOnlyList<EvalResult> results)
    {
        var reasons = results
            .Select(r => (r.Case.Id, Reason: r.Error is not null
                ? "errored: " + r.Error
                : r.InvocationFailurePerRepeat.FirstOrDefault(f => f is not null) is { } failure
                    ? "invocation failed: " + failure
                    : null))
            .Where(r => r.Reason is not null)
            .ToList();
        if (reasons.Count == 0)
            return;

        console.WriteLine();
        console.MarkupLine("[bold]Reasons[/]");
        foreach (var (caseId, reason) in reasons)
            console.MarkupLine($"  {Markup.Escape(caseId)}: {Markup.Escape(OneLine(reason!))}");
    }

    private static string OneLine(string text)
    {
        const int maxLength = 200;
        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? trimmed : trimmed[..newline].TrimEnd();
        if (line.Length > maxLength)
            line = line[..maxLength];
        return line.Length < trimmed.Length ? line + " …" : line;
    }
}
