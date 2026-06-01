namespace Infrastructure.AI.Evaluation.Persistence;

/// <summary>
/// EF-mapped row for one metric's aggregated (median-across-repeats) score within
/// a case result. Exists so Step 5.4.3 read queries can aggregate over scores
/// efficiently in SQL without parsing JSON blobs.
/// </summary>
/// <remarks>
/// The composite index <c>(RunId, MetricKey)</c> supports the prompt-version
/// comparison query: join <c>eval_metric_scores</c> on <c>(case_id, metric_key)</c>
/// against <c>prompt_usage</c>, group by <c>prompt_version</c>, and average.
/// </remarks>
public sealed class EvalMetricScoreEntity
{
    /// <summary>Autoincrement surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>Run identifier (denormalised — equals the parent case's <c>RunId</c>).</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Case identifier the score belongs to.</summary>
    public string CaseId { get; set; } = string.Empty;

    /// <summary>Metric key (matches an <c>IEvalMetric</c> keyed-DI registration).</summary>
    public string MetricKey { get; set; } = string.Empty;

    /// <summary>Aggregated score (median across repeats).</summary>
    public double Score { get; set; }

    /// <summary>Verdict as <c>Verdict</c> enum integer.</summary>
    public int Verdict { get; set; }

    /// <summary>Optional human-readable explanation (LLM judges, etc.).</summary>
    public string? Reasoning { get; set; }

    /// <summary>Aggregated cost in USD for producing this metric's score on this case.</summary>
    public decimal CostUsd { get; set; }

    /// <summary>Aggregated wall-clock duration in 100-ns ticks.</summary>
    public long DurationTicks { get; set; }
}
