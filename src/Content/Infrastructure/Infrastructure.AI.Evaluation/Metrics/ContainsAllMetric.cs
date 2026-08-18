using System.Diagnostics;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Infrastructure.AI.Evaluation.Metrics;

/// <summary>
/// Scores the fraction of pipe-separated <c>values</c> that appear in the agent's output.
/// Case-insensitive by default; toggle via <c>case_sensitive</c> parameter.
/// </summary>
/// <remarks>
/// Returns 1.0 when every value is found, 0.0 when none. Partial credit (e.g. 2/3 = 0.667)
/// for fractional matches — useful when scoring tolerance for missing keywords.
/// Verdict against <see cref="MetricSpec.Threshold"/>.
/// </remarks>
public sealed class ContainsAllMetric : IEvalMetric
{
    private const string ValuesKey = "values";
    private const string CaseSensitiveKey = "case_sensitive";

    /// <inheritdoc />
    public string Key => "contains_all";

    /// <inheritdoc />
    public IReadOnlySet<string> RecognizedParameterKeys { get; } =
        new HashSet<string> { ValuesKey, CaseSensitiveKey };

    /// <inheritdoc />
    public Task<MetricScore> ScoreAsync(
        EvalCase @case,
        AgentInvocationResult output,
        MetricSpec spec,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (!spec.Parameters.TryGetValue(ValuesKey, out var valuesRaw) || string.IsNullOrWhiteSpace(valuesRaw))
        {
            sw.Stop();
            return Task.FromResult(new MetricScore
            {
                MetricKey = Key,
                Score = 0.0,
                Verdict = Verdict.Warn,
                Reasoning = "Missing required 'values' parameter (pipe-separated).",
                Duration = sw.Elapsed
            });
        }

        var caseSensitive = spec.Parameters.TryGetValue(CaseSensitiveKey, out var cs)
            && bool.TryParse(cs, out var parsed) && parsed;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var values = valuesRaw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            sw.Stop();
            return Task.FromResult(new MetricScore
            {
                MetricKey = Key,
                Score = 0.0,
                Verdict = Verdict.Warn,
                Reasoning = "'values' parameter was empty after splitting.",
                Duration = sw.Elapsed
            });
        }

        var found = values.Count(v => output.Output.Contains(v, comparison));
        var score = (double)found / values.Length;
        sw.Stop();

        var verdict = score >= spec.Threshold ? Verdict.Pass : Verdict.Fail;
        return Task.FromResult(new MetricScore
        {
            MetricKey = Key,
            Score = score,
            Verdict = verdict,
            Reasoning = $"{found}/{values.Length} values found.",
            Duration = sw.Elapsed
        });
    }
}
