using System.Diagnostics;
using System.Text;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Infrastructure.AI.Evaluation.Metrics;

/// <summary>
/// Scores 1.0 when a router's predicted label matches the case's gold
/// <see cref="EvalCase.ExpectedOutput"/>, else 0.0. The predicted label arrives as the
/// invocation <see cref="AgentInvocationResult.Output"/> from a router-target case
/// (<c>target: "router:&lt;key&gt;"</c>).
/// </summary>
/// <remarks>
/// <para>
/// Comparison is tolerant of enum formatting: both sides are normalized to lowercase with all
/// non-alphanumeric characters removed, so a gold value of <c>MultiHop</c>, <c>multi_hop</c>, or
/// <c>multihop</c> all match the router's <c>MultiHop</c>.
/// </para>
/// <para>
/// Fail-soft per the <see cref="IEvalMetric"/> contract: returns <see cref="Verdict.Warn"/> (not
/// <see cref="Verdict.Fail"/>) when the case has no expected label, or when the invocation itself
/// failed — an errored router is an availability problem, distinct from a wrong classification, and
/// should not silently count as an accuracy miss.
/// </para>
/// </remarks>
public sealed class RoutingAccuracyMetric : IEvalMetric
{
    /// <inheritdoc />
    public string Key => "routing_accuracy";

    /// <inheritdoc />
    public Task<MetricScore> ScoreAsync(
        EvalCase @case,
        AgentInvocationResult output,
        MetricSpec spec,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (@case.ExpectedOutput is null)
        {
            return Warn(sw, "Case has no expected_output; routing_accuracy cannot score.");
        }

        if (!output.Success)
        {
            return Warn(sw, $"Router invocation did not succeed; cannot score routing. {output.Error}".TrimEnd());
        }

        var predicted = Normalize(output.Output);
        var expected = Normalize(@case.ExpectedOutput);
        var matches = predicted.Length > 0 && string.Equals(predicted, expected, StringComparison.Ordinal);

        sw.Stop();
        return Task.FromResult(new MetricScore
        {
            MetricKey = Key,
            Score = matches ? 1.0 : 0.0,
            Verdict = matches ? Verdict.Pass : Verdict.Fail,
            Reasoning = matches
                ? $"Routed to '{output.Output}' as expected."
                : $"Routed to '{output.Output}', expected '{@case.ExpectedOutput}'.",
            Duration = sw.Elapsed
        });
    }

    private Task<MetricScore> Warn(Stopwatch sw, string reasoning)
    {
        sw.Stop();
        return Task.FromResult(new MetricScore
        {
            MetricKey = Key,
            Score = 0.0,
            Verdict = Verdict.Warn,
            Reasoning = reasoning,
            Duration = sw.Elapsed
        });
    }

    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }
}
