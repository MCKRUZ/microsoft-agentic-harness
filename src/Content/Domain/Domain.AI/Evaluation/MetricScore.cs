namespace Domain.AI.Evaluation;

/// <summary>
/// The outcome of evaluating a single metric against one case's output.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Score"/> is metric-defined but conventionally ranges 0.0 (worst) to 1.0 (best).
/// </para>
/// <para>
/// <see cref="Verdict"/> is the metric's own pass/fail decision after comparing
/// <see cref="Score"/> to its threshold. Aggregation across metrics happens at the
/// <see cref="EvalResult"/> level.
/// </para>
/// <para>
/// <see cref="Reasoning"/> is human-readable explanation, especially valuable for
/// LLM-judge metrics where the rationale aids debugging. <see cref="RawOutput"/>
/// preserves the metric's raw response for forensic review.
/// </para>
/// </remarks>
public sealed record MetricScore
{
    /// <summary>The metric key that produced this score (e.g. "exact_match", "faithfulness").</summary>
    public required string MetricKey { get; init; }

    /// <summary>The numeric score. Conventionally 0.0–1.0; metric-defined.</summary>
    public required double Score { get; init; }

    /// <summary>The metric's pass/fail/warn verdict after threshold comparison.</summary>
    public required Verdict Verdict { get; init; }

    /// <summary>Optional human-readable explanation of the score.</summary>
    public string? Reasoning { get; init; }

    /// <summary>
    /// Optional raw output from the metric (e.g. the LLM judge's full response).
    /// Preserved for debugging; not displayed in summary reports.
    /// </summary>
    public string? RawOutput { get; init; }

    /// <summary>
    /// Cost in USD of producing this score, when known. Zero for non-LLM metrics.
    /// Surfaces in cost-tracking reports.
    /// </summary>
    public decimal CostUsd { get; init; }

    /// <summary>How long the metric took to score this case.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// When a panel of judges (a "jury") produced this score, how much they agreed.
    /// <c>null</c> for single-judge metrics and all non-LLM metrics — the dashboard
    /// renders those as no consensus indicator.
    /// </summary>
    /// <remarks>
    /// Advisory only: the pass/fail <see cref="Verdict"/> is decided from the aggregated
    /// (median) score versus the metric threshold, independent of this bucket. The
    /// per-panelist breakdown is preserved in <see cref="RawOutput"/> for forensic review.
    /// </remarks>
    public ConsensusBucket? Consensus { get; init; }

    /// <summary>
    /// The spread (max − min) of the panelists' individual scores when a jury produced
    /// this score; <c>null</c> for single-judge and non-LLM metrics. Drives the
    /// <see cref="Consensus"/> bucket and is shown alongside it on the dashboard.
    /// </summary>
    public double? Spread { get; init; }
}
