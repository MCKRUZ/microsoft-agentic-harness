namespace Domain.Common.Config.AI.ContextManagement;

/// <summary>
/// Configuration for context compaction triggers, circuit breaker behavior,
/// and strategy-specific limits. Bound from <c>AppConfig:AI:ContextManagement:Compaction</c>
/// in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Compaction runs in two modes:
/// <list type="bullet">
///   <item><description>Micro-compact: evicts stale tool results exceeding <see cref="MicroCompactStalenessMinutes"/>.</description></item>
///   <item><description>Full compact: summarizes conversation history, preserving plans and skills within token budgets.</description></item>
/// </list>
/// A circuit breaker prevents runaway compaction attempts when the LLM repeatedly fails to produce
/// a valid summary.
/// </para>
/// </remarks>
public class CompactionConfig
{
    /// <summary>
    /// Gets or sets the ratio of token usage to max context window that triggers auto-compaction.
    /// For example, 0.85 means compaction triggers when 85% of the context window is consumed.
    /// </summary>
    public double AutoCompactThresholdRatio { get; set; } = 0.85;

    /// <summary>
    /// Gets or sets the maximum number of consecutive compaction failures before the circuit
    /// breaker trips and stops further attempts.
    /// </summary>
    public int CircuitBreakerMaxFailures { get; set; } = 3;

    /// <summary>
    /// Gets or sets the cooldown period in seconds after which a tripped circuit breaker resets
    /// and allows compaction attempts again.
    /// </summary>
    public int CircuitBreakerCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the staleness threshold in minutes for micro-compaction. Tool results older
    /// than this value become candidates for eviction during micro-compact passes.
    /// </summary>
    public int MicroCompactStalenessMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of plan files preserved during a full compaction.
    /// Plans exceeding this count are summarized into a single overview.
    /// </summary>
    public int FullCompactMaxPreservedPlans { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum token budget per preserved plan during full compaction.
    /// Plans exceeding this limit are truncated to their summary section.
    /// </summary>
    public int FullCompactPlanTokenBudget { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the maximum total token budget for all preserved skills during full compaction.
    /// Skills are prioritized by recency and relevance when the budget is exceeded.
    /// </summary>
    public int FullCompactSkillTokenBudget { get; set; } = 25000;
}
