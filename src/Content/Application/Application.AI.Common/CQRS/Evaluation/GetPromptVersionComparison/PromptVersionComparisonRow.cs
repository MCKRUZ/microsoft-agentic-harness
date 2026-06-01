using Domain.AI.Prompts;

namespace Application.AI.Common.CQRS.Evaluation.GetPromptVersionComparison;

/// <summary>
/// One row of the prompt-version comparison report: the aggregated eval-score
/// signal for a specific (prompt, version, metric) tuple across every case the
/// version was used on.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SampleSize"/> is the number of distinct cases that contributed to
/// <see cref="AverageScore"/>. Versions with fewer than the caller's stability
/// threshold can be greyed out in the UI to signal "not enough data."
/// </para>
/// </remarks>
public sealed record PromptVersionComparisonRow
{
    /// <summary>The prompt version this row aggregates.</summary>
    public required PromptVersion Version { get; init; }

    /// <summary>The metric key whose scores were averaged.</summary>
    public required string MetricKey { get; init; }

    /// <summary>Arithmetic mean of the per-case scores for this version + metric.</summary>
    public required double AverageScore { get; init; }

    /// <summary>Number of distinct cases that contributed to the average.</summary>
    public required int SampleSize { get; init; }
}
