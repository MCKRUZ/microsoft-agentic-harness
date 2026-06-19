using Domain.AI.Evaluation;

namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// The aggregated outcome of a panel of judges (a "jury") scoring one case: the
/// per-panelist verdicts plus the consensus summary derived from them.
/// </summary>
/// <remarks>
/// <para>
/// Produced by <c>JuryAggregator</c> and attached to <see cref="LlmJudgeResult.Panel"/>.
/// The aggregated score itself lives on <see cref="LlmJudgeResult.Score"/> (the median of
/// the responding panelists); this record carries the agreement signal that the metric
/// copies onto <see cref="MetricScore.Consensus"/> / <see cref="MetricScore.Spread"/> and
/// the full per-panelist breakdown for forensics.
/// </para>
/// <para>
/// <c>null</c> on a <see cref="LlmJudgeResult"/> means a single judge produced the score
/// (no panel configured).
/// </para>
/// </remarks>
public sealed record JuryPanelResult
{
    /// <summary>Every panelist's verdict, including those excluded from the aggregate (non-Parsed).</summary>
    public required IReadOnlyList<PanelistVerdict> Verdicts { get; init; }

    /// <summary>How much the responding panelists agreed, bucketed from <see cref="Spread"/>.</summary>
    public required ConsensusBucket Bucket { get; init; }

    /// <summary>The spread (max − min) of the responding panelists' scores.</summary>
    public required double Spread { get; init; }

    /// <summary>How many panelists returned a usable (Parsed) score and contributed to the aggregate.</summary>
    public required int Responded { get; init; }

    /// <summary>How many panelists were excluded because their call failed or returned malformed JSON.</summary>
    public required int Excluded { get; init; }
}
