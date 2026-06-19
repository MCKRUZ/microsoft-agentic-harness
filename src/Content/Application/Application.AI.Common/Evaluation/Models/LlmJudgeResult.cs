namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// The structured outcome of asking the LLM judge to score one item.
/// Returned by <see cref="Interfaces.ILlmJudge.JudgeAsync"/> and consumed by
/// metric implementations to build a <see cref="Domain.AI.Evaluation.MetricScore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Verdict"/> distinguishes a successful score (<see cref="Outcomes.LlmJudgeOutcome.Parsed"/>)
/// from a soft failure (<see cref="Outcomes.LlmJudgeOutcome.Malformed"/> after retry,
/// or <see cref="Outcomes.LlmJudgeOutcome.InvocationFailed"/> on infra error). Callers
/// translate the outcome to the appropriate <c>Verdict</c> in their <c>MetricScore</c>.
/// </para>
/// <para>
/// <see cref="Score"/> is <c>0.0</c> on any non-Parsed outcome so consumers can
/// safely aggregate without null checks.
/// </para>
/// </remarks>
public sealed record LlmJudgeResult
{
    /// <summary>Why this result terminated — parsed, malformed-after-retry, infra failure.</summary>
    public required Outcomes.LlmJudgeOutcome Outcome { get; init; }

    /// <summary>The parsed numeric score in [0, 1]. Always <c>0.0</c> on non-Parsed outcomes.</summary>
    public required double Score { get; init; }

    /// <summary>Optional reasoning from the judge (parsed from the JSON <c>reasoning</c> field).</summary>
    public string? Reasoning { get; init; }

    /// <summary>The raw model response from the last attempt (parsed or otherwise). Useful for debugging.</summary>
    public string? RawOutput { get; init; }

    /// <summary>USD cost of producing this judgment, computed from token usage × configured rates.</summary>
    public decimal CostUsd { get; init; }

    /// <summary>Total input tokens consumed across all attempts.</summary>
    public long InputTokens { get; init; }

    /// <summary>Total output tokens produced across all attempts.</summary>
    public long OutputTokens { get; init; }

    /// <summary>
    /// When a panel of judges (a "jury") produced this result, the per-panelist
    /// breakdown and consensus summary; <see cref="Score"/> is then the aggregate.
    /// <c>null</c> when a single judge produced the result (the default).
    /// </summary>
    /// <remarks>
    /// Additive and back-compatible: existing single-judge callers ignore this field.
    /// Metrics copy <see cref="JuryPanelResult.Bucket"/> / <see cref="JuryPanelResult.Spread"/>
    /// onto their <c>MetricScore</c> for the dashboard.
    /// </remarks>
    public JuryPanelResult? Panel { get; init; }
}
