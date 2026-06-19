using Application.AI.Common.Evaluation.Outcomes;

namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// One panelist's contribution to a jury score — the result of a single judge model
/// (optionally wearing a persona "lens") scoring the case.
/// </summary>
/// <remarks>
/// Aggregated across the panel by <c>JuryAggregator</c> into a
/// <see cref="JuryPanelResult"/>. A panelist whose call failed or returned malformed
/// JSON carries a non-<see cref="LlmJudgeOutcome.Parsed"/> <see cref="Outcome"/> and is
/// excluded from the aggregate score (its cost is still counted).
/// </remarks>
public sealed record PanelistVerdict
{
    /// <summary>The panelist's configured label (e.g. "gpt-4o", "security-lens").</summary>
    public required string Name { get; init; }

    /// <summary>The panelist's score in [0, 1]. <c>0.0</c> on any non-Parsed outcome.</summary>
    public required double Score { get; init; }

    /// <summary>Why this panelist's call terminated — parsed, malformed-after-retry, or infra failure.</summary>
    public required LlmJudgeOutcome Outcome { get; init; }

    /// <summary>Optional reasoning this panelist returned. <c>null</c> when the call did not parse.</summary>
    public string? Reasoning { get; init; }

    /// <summary>USD cost of this panelist's call. Counted toward the jury total even when excluded from the score.</summary>
    public decimal CostUsd { get; init; }
}
