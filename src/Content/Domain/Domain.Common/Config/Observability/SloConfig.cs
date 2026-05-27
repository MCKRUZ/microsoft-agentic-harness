namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for the SLO (Service Level Objective) board that continuously
/// evaluates operational health targets against live Prometheus metrics.
/// When enabled, the <c>/api/metrics/slo</c> endpoint returns the current
/// verdict (Met / AtRisk / Breached) and error-budget remaining for each target.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="SloTargetConfig"/> pairs a PromQL instant query with a
/// threshold comparator. The evaluation service runs the query, parses the
/// scalar result, and compares it to produce a verdict:
/// <list type="bullet">
///   <item><description><b>Met</b> -- current value satisfies the target and is below the warning threshold.</description></item>
///   <item><description><b>AtRisk</b> -- current value satisfies the target but exceeds the warning threshold.</description></item>
///   <item><description><b>Breached</b> -- current value violates the target.</description></item>
/// </list>
/// </para>
/// <para>
/// Template consumers add new SLOs by appending entries to
/// <c>AppConfig:Observability:Slo:Targets</c> in <c>appsettings.json</c>.
/// </para>
/// </remarks>
public class SloConfig
{
    /// <summary>
    /// Gets or sets whether SLO evaluation is enabled.
    /// When <c>false</c>, the <c>/api/metrics/slo</c> endpoint returns an empty list.
    /// </summary>
    /// <value>Default: <c>false</c>.</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the list of SLO targets to evaluate.
    /// Each target defines a PromQL query, threshold, and comparator.
    /// </summary>
    /// <value>Default: empty list.</value>
    public List<SloTargetConfig> Targets { get; set; } = [];
}

/// <summary>
/// A single SLO target definition pairing a PromQL query with a threshold
/// comparator and error-budget allowance.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="ValueQuery"/> must return a scalar (single-element vector)
/// from Prometheus. Multi-series results use only the first series' value.
/// </para>
/// <para>
/// Error budget is expressed as a fraction: <c>0.01</c> means 1% of the
/// <see cref="Window"/> may be spent in a breached state before the budget
/// is exhausted. The dashboard renders budget remaining as a percentage bar.
/// </para>
/// </remarks>
public class SloTargetConfig
{
    /// <summary>
    /// Gets or sets the unique identifier for this SLO target.
    /// Used as the key in API responses and dashboard rendering.
    /// </summary>
    /// <example><c>"p95-latency"</c>, <c>"error-rate"</c>.</example>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable name displayed in the SLO board.
    /// </summary>
    /// <example><c>"P95 Turn Latency"</c>.</example>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a description of what this SLO measures and why it matters.
    /// </summary>
    /// <example><c>"95th percentile agent turn latency stays below 2000ms"</c>.</example>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the PromQL instant query that produces the current metric value.
    /// Must return a scalar or single-element vector result.
    /// </summary>
    /// <example><c>"histogram_quantile(0.95, rate(agentic_harness_agent_orchestration_turn_duration_ms_bucket[5m]))"</c>.</example>
    public string ValueQuery { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unit of the metric value for display formatting.
    /// Common values: <c>"ms"</c>, <c>"percent"</c>, <c>"count"</c>.
    /// </summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the comparison operator applied to the current value against <see cref="Target"/>.
    /// Supported values: <c>"lt"</c> (less than), <c>"gt"</c> (greater than),
    /// <c>"lte"</c> (less than or equal), <c>"gte"</c> (greater than or equal).
    /// </summary>
    public string Comparator { get; set; } = "lt";

    /// <summary>
    /// Gets or sets the target threshold value. The SLO is <b>Met</b> when the
    /// current value satisfies the <see cref="Comparator"/> against this value.
    /// </summary>
    /// <example><c>2000</c> (ms), <c>0.05</c> (5% error rate).</example>
    public double Target { get; set; }

    /// <summary>
    /// Gets or sets the warning threshold. When the current value is between
    /// this threshold and <see cref="Target"/>, the SLO verdict is <b>AtRisk</b>.
    /// Must be stricter than <see cref="Target"/> (e.g., lower for <c>"lt"</c> comparators).
    /// </summary>
    public double WarningThreshold { get; set; }

    /// <summary>
    /// Gets or sets the evaluation window as a duration string.
    /// Informational for the dashboard; the actual query window is embedded in <see cref="ValueQuery"/>.
    /// </summary>
    /// <value>Default: <c>"24h"</c>.</value>
    public string Window { get; set; } = "24h";

    /// <summary>
    /// Gets or sets the error budget as a fraction of the evaluation window.
    /// <c>0.01</c> means 1% of the window may be spent in a breached state.
    /// </summary>
    /// <value>Default: <c>0.01</c> (1%).</value>
    public double ErrorBudgetPercent { get; set; } = 0.01;
}
