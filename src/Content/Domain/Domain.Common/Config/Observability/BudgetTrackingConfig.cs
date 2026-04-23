namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for LLM cost budget tracking with automatic alerting.
/// Implements a state machine (Clear/Warning/Critical) with hysteresis
/// to prevent alert flapping near threshold boundaries.
/// </summary>
/// <remarks>
/// <para>
/// Budget tracking evaluates cumulative spend per period against configured
/// thresholds. Period rollovers reset the spend accumulator:
/// daily (midnight UTC), weekly (Monday midnight UTC), monthly (1st midnight UTC).
/// </para>
/// <para>
/// Hysteresis prevents flapping: escalation occurs at the threshold,
/// but de-escalation requires spend to drop below <see cref="HysteresisPercent"/>
/// of the threshold (e.g., 90% of $50 = $45 to clear a $50 warning).
/// </para>
/// </remarks>
public class BudgetTrackingConfig
{
    /// <summary>Gets or sets whether budget tracking is enabled.</summary>
    /// <value>Default: <c>false</c>.</value>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the daily budget limit in USD.</summary>
    /// <value>Default: 50.00.</value>
    public decimal DailyBudgetUsd { get; set; } = 50.00m;

    /// <summary>Gets or sets the weekly budget limit in USD.</summary>
    /// <value>Default: 250.00.</value>
    public decimal WeeklyBudgetUsd { get; set; } = 250.00m;

    /// <summary>Gets or sets the monthly budget limit in USD.</summary>
    /// <value>Default: 1000.00.</value>
    public decimal MonthlyBudgetUsd { get; set; } = 1000.00m;

    /// <summary>
    /// Gets or sets the warning threshold as a fraction of the budget (0-1).
    /// When spend exceeds this fraction, status escalates to Warning.
    /// </summary>
    /// <value>Default: 0.75 (75%).</value>
    public double WarningThresholdPercent { get; set; } = 0.75;

    /// <summary>
    /// Gets or sets the critical threshold as a fraction of the budget (0-1).
    /// When spend exceeds this fraction, status escalates to Critical.
    /// </summary>
    /// <value>Default: 0.90 (90%).</value>
    public double CriticalThresholdPercent { get; set; } = 0.90;

    /// <summary>
    /// Gets or sets the hysteresis de-escalation factor (0-1).
    /// Status de-escalates when spend drops below this fraction of the escalation threshold.
    /// </summary>
    /// <value>Default: 0.90 (de-escalate at 90% of the threshold that triggered escalation).</value>
    public double HysteresisPercent { get; set; } = 0.90;
}
