namespace Application.AI.Common.CQRS.Evaluation.GetTopRegressedCases;

/// <summary>
/// One row of the top-regressed-cases report: a case whose aggregated score
/// dropped between the baseline run and the current run on a given metric.
/// </summary>
public sealed record RegressedCaseRow
{
    /// <summary>The case identifier.</summary>
    public required string CaseId { get; init; }

    /// <summary>The dataset the case belonged to (denormalised at ingest).</summary>
    public required string DatasetName { get; init; }

    /// <summary>The metric whose score regressed.</summary>
    public required string MetricKey { get; init; }

    /// <summary>The aggregated score from the baseline run.</summary>
    public required double BaselineScore { get; init; }

    /// <summary>The aggregated score from the current run.</summary>
    public required double CurrentScore { get; init; }

    /// <summary>
    /// Delta = <see cref="CurrentScore"/> − <see cref="BaselineScore"/>. Negative for a
    /// regression. Surfaced as its own property so the UI can colour-code without
    /// recomputing.
    /// </summary>
    public double Delta => CurrentScore - BaselineScore;
}
