using System.Globalization;
using System.Xml;
using Application.AI.Common.Evaluation.Interfaces;
using Domain.AI.Evaluation;

namespace Infrastructure.AI.Evaluation.Reporters;

/// <summary>
/// Serializes an <see cref="EvalRunReport"/> as JUnit XML so CI systems
/// (GitHub Actions, Azure DevOps, Jenkins) can render eval results as test results.
/// </summary>
/// <remarks>
/// <para>
/// Emits one <c>&lt;testsuite&gt;</c> per <see cref="EvalDataset"/>, with one
/// <c>&lt;testcase&gt;</c> per case. Mapping:
/// <list type="bullet">
///   <item><description><see cref="Verdict.Fail"/> → <c>&lt;failure&gt;</c></description></item>
///   <item><description><see cref="Verdict.Warn"/> → <c>&lt;skipped&gt;</c> with the warn reason as message</description></item>
///   <item><description>Case with <c>Error</c> set → <c>&lt;error&gt;</c></description></item>
///   <item><description><see cref="Verdict.Pass"/> → no inner element</description></item>
///   <item><description>Case declared in the dataset but missing from results (e.g. cancelled mid-run) → <c>&lt;skipped message="not executed"&gt;</c></description></item>
/// </list>
/// </para>
/// <para>
/// Duplicate case ids (e.g. the same dataset path passed twice in <c>DatasetPaths</c>)
/// are collapsed via last-write-wins so the reporter does not crash mid-write.
/// </para>
/// <para>
/// The passed count is emitted as a JUnit-XSD-legal <c>&lt;property name="passed"&gt;</c>
/// child rather than a non-standard root-level attribute, so strict consumers
/// (Jenkins junit-plugin strict, xmllint+junit-10.xsd) accept the output.
/// </para>
/// </remarks>
public sealed class JUnitXmlEvalReporter : IEvalReporter
{
    /// <inheritdoc />
    public string FormatKey => "junit";

    /// <inheritdoc />
    public async Task WriteAsync(
        EvalRunReport report,
        Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        var settings = new XmlWriterSettings
        {
            Async = true,
            Indent = true,
            IndentChars = "  ",
            Encoding = new System.Text.UTF8Encoding(false),
            OmitXmlDeclaration = false
        };

        await using var writer = XmlWriter.Create(output, settings);

        // Last-write-wins on duplicate case ids — guards against operator mistakes
        // (same dataset listed twice) or authoring mistakes (duplicate id in YAML).
        var resultsByCaseId = new Dictionary<string, EvalResult>(report.Results.Count, StringComparer.Ordinal);
        foreach (var r in report.Results)
        {
            resultsByCaseId[r.Case.Id] = r;
        }

        var totalDuration = report.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        var totalTests = report.PassedCount + report.FailedCount + report.WarnedCount + report.ErroredCount;

        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "testsuites", null).ConfigureAwait(false);
        await WriteAttrAsync(writer, "name", $"eval-run-{report.RunId}").ConfigureAwait(false);
        await WriteIntAttrAsync(writer, "tests", totalTests).ConfigureAwait(false);
        await WriteIntAttrAsync(writer, "failures", report.FailedCount).ConfigureAwait(false);
        await WriteIntAttrAsync(writer, "errors", report.ErroredCount).ConfigureAwait(false);
        await WriteIntAttrAsync(writer, "skipped", report.WarnedCount).ConfigureAwait(false);
        await WriteAttrAsync(writer, "time", totalDuration).ConfigureAwait(false);
        await WritePassedPropertyAsync(writer, report.PassedCount).ConfigureAwait(false);

        foreach (var dataset in report.Datasets)
        {
            await WriteSuiteAsync(writer, dataset, resultsByCaseId, cancellationToken).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteSuiteAsync(
        XmlWriter writer,
        EvalDataset dataset,
        IReadOnlyDictionary<string, EvalResult> resultsByCaseId,
        CancellationToken cancellationToken)
    {
        // Single-pass: collect matching results AND track missing cases so we can
        // emit a synthetic <skipped> entry for each — keeps the suite's <testcase>
        // count equal to dataset.Cases.Count (partial runs don't silently drop cases).
        var present = new List<EvalResult>(dataset.Cases.Count);
        var missing = new List<EvalCase>();
        int passed = 0, failed = 0, warned = 0, errored = 0;
        var totalDuration = TimeSpan.Zero;

        foreach (var c in dataset.Cases)
        {
            if (resultsByCaseId.TryGetValue(c.Id, out var r))
            {
                present.Add(r);
                totalDuration += r.Duration;

                if (r.Error is not null) errored++;
                else if (r.Verdict == Verdict.Fail) failed++;
                else if (r.Verdict == Verdict.Warn) warned++;
                else passed++;
            }
            else
            {
                missing.Add(c);
                warned++;
            }
        }

        var totalCases = dataset.Cases.Count;
        var suiteDuration = totalDuration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

        await writer.WriteStartElementAsync(null, "testsuite", null).ConfigureAwait(false);
        await WriteAttrAsync(writer, "name", dataset.Name).ConfigureAwait(false);
        await WriteIntAttrAsync(writer, "tests", totalCases).ConfigureAwait(false);
        await WriteIntAttrAsync(writer, "failures", failed).ConfigureAwait(false);
        await WriteIntAttrAsync(writer, "errors", errored).ConfigureAwait(false);
        await WriteIntAttrAsync(writer, "skipped", warned).ConfigureAwait(false);
        await WriteAttrAsync(writer, "time", suiteDuration).ConfigureAwait(false);
        await WritePassedPropertyAsync(writer, passed).ConfigureAwait(false);

        foreach (var result in present)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteCaseAsync(writer, dataset.Name, result).ConfigureAwait(false);
        }

        foreach (var c in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteMissingCaseAsync(writer, dataset.Name, c).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteCaseAsync(XmlWriter writer, string suiteName, EvalResult result)
    {
        var caseDuration = result.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

        await writer.WriteStartElementAsync(null, "testcase", null).ConfigureAwait(false);
        await WriteAttrAsync(writer, "name", result.Case.Id).ConfigureAwait(false);
        await WriteAttrAsync(writer, "classname", suiteName).ConfigureAwait(false);
        await WriteAttrAsync(writer, "time", caseDuration).ConfigureAwait(false);

        if (result.Error is not null)
        {
            await writer.WriteStartElementAsync(null, "error", null).ConfigureAwait(false);
            await WriteAttrAsync(writer, "message", result.Error).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }
        else if (result.Verdict == Verdict.Fail)
        {
            await writer.WriteStartElementAsync(null, "failure", null).ConfigureAwait(false);
            await WriteAttrAsync(writer, "message", FormatFailureMessage(result)).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }
        else if (result.Verdict == Verdict.Warn)
        {
            await writer.WriteStartElementAsync(null, "skipped", null).ConfigureAwait(false);
            await WriteAttrAsync(writer, "message", FormatFailureMessage(result)).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteMissingCaseAsync(XmlWriter writer, string suiteName, EvalCase missing)
    {
        await writer.WriteStartElementAsync(null, "testcase", null).ConfigureAwait(false);
        await WriteAttrAsync(writer, "name", missing.Id).ConfigureAwait(false);
        await WriteAttrAsync(writer, "classname", suiteName).ConfigureAwait(false);
        await WriteAttrAsync(writer, "time", "0.000").ConfigureAwait(false);

        await writer.WriteStartElementAsync(null, "skipped", null).ConfigureAwait(false);
        await WriteAttrAsync(writer, "message", "Case was declared but not executed (e.g. run cancelled or filtered).").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);

        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WritePassedPropertyAsync(XmlWriter writer, int passed)
    {
        await writer.WriteStartElementAsync(null, "properties", null).ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "property", null).ConfigureAwait(false);
        await WriteAttrAsync(writer, "name", "passed").ConfigureAwait(false);
        await WriteAttrAsync(writer, "value", passed.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static Task WriteAttrAsync(XmlWriter writer, string name, string value)
        => writer.WriteAttributeStringAsync(null, name, null, value);

    private static Task WriteIntAttrAsync(XmlWriter writer, string name, int value)
        => writer.WriteAttributeStringAsync(null, name, null, value.ToString(CultureInfo.InvariantCulture));

    private static string FormatFailureMessage(EvalResult result)
    {
        var offending = result.AggregatedScores.Values
            .Where(s => s.Verdict != Verdict.Pass)
            .Select(s => $"{s.MetricKey}={s.Score.ToString("F3", CultureInfo.InvariantCulture)}({s.Verdict})");
        var joined = string.Join("; ", offending);
        var message = string.IsNullOrEmpty(joined)
            ? result.Verdict.ToString()
            : joined;

        // A scored case whose invocation failed (a refusal) is a <failure>, not an <error>; without its reason the
        // message shows only a zero score.
        return result.InvocationFailurePerRepeat.FirstOrDefault(f => f is not null) is { } failure
            ? $"{message}; invocation failed: {failure}"
            : message;
    }
}
