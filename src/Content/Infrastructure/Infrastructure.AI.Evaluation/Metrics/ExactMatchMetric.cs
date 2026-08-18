using System.Diagnostics;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Infrastructure.AI.Evaluation.Metrics;

/// <summary>
/// Scores 1.0 when the agent's output is byte-equal to the case's
/// <see cref="EvalCase.ExpectedOutput"/>, else 0.0.
/// </summary>
/// <remarks>
/// Supports a <c>case_sensitive</c> parameter (default true). Returns
/// <see cref="Verdict.Warn"/> when the case has no expected output, since
/// exact-match is meaningless without one.
/// </remarks>
public sealed class ExactMatchMetric : IEvalMetric
{
    private const string CaseSensitiveKey = "case_sensitive";

    /// <inheritdoc />
    public string Key => "exact_match";

    /// <inheritdoc />
    public IReadOnlySet<string> RecognizedParameterKeys { get; } = new HashSet<string> { CaseSensitiveKey };

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
            sw.Stop();
            return Task.FromResult(new MetricScore
            {
                MetricKey = Key,
                Score = 0.0,
                Verdict = Verdict.Warn,
                Reasoning = "Case has no expected_output; exact_match cannot score.",
                Duration = sw.Elapsed
            });
        }

        var caseSensitive = !spec.Parameters.TryGetValue(CaseSensitiveKey, out var cs)
            || !bool.TryParse(cs, out var parsed) || parsed;

        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var matches = string.Equals(output.Output, @case.ExpectedOutput, comparison);
        sw.Stop();

        return Task.FromResult(new MetricScore
        {
            MetricKey = Key,
            Score = matches ? 1.0 : 0.0,
            Verdict = matches ? Verdict.Pass : Verdict.Fail,
            Reasoning = matches ? "Output equals expected." : "Output differs from expected.",
            Duration = sw.Elapsed
        });
    }
}
