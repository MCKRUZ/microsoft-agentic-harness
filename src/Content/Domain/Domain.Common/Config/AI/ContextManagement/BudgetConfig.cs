namespace Domain.Common.Config.AI.ContextManagement;

/// <summary>
/// Configuration for diminishing returns detection and context budget completion thresholds.
/// Bound from <c>AppConfig:AI:ContextManagement:Budget</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// The budget system monitors token usage across continuations. When successive continuations
/// produce fewer new tokens than <see cref="DiminishingReturnsMinDelta"/>, the agent signals
/// that further continuations are unlikely to produce meaningful progress.
/// </para>
/// </remarks>
public class BudgetConfig
{
    /// <summary>
    /// Gets or sets the number of continuations after which the system begins checking
    /// for diminishing returns. The first N continuations are assumed productive.
    /// </summary>
    public int DiminishingReturnsContinuationThreshold { get; set; } = 3;

    /// <summary>
    /// Gets or sets the minimum number of new tokens a continuation must produce
    /// to be considered productive. Below this delta, the continuation is flagged
    /// as diminishing returns.
    /// </summary>
    public int DiminishingReturnsMinDelta { get; set; } = 500;

    /// <summary>
    /// Gets or sets the ratio of budget consumed that signals near-completion.
    /// For example, 0.90 means the agent should wrap up when 90% of the context
    /// budget has been used.
    /// </summary>
    public double CompletionThresholdRatio { get; set; } = 0.90;
}
