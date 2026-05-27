namespace Application.Common.Models;

/// <summary>
/// The evaluated status of a single SLO target at a point in time.
/// Produced by the SLO evaluator and returned by the <c>/api/metrics/slo</c> endpoint.
/// </summary>
/// <param name="Id">Unique identifier matching the configured <c>SloTargetConfig.Id</c>.</param>
/// <param name="Name">Human-readable name for dashboard display.</param>
/// <param name="Description">Explanation of what this SLO measures and why it matters.</param>
/// <param name="Target">The threshold value the metric is compared against.</param>
/// <param name="CurrentValue">The current metric value from Prometheus. <c>-1</c> when the query failed.</param>
/// <param name="Unit">Display unit (<c>"ms"</c>, <c>"percent"</c>, <c>"count"</c>).</param>
/// <param name="Comparator">Comparison operator (<c>"lt"</c>, <c>"gt"</c>, <c>"lte"</c>, <c>"gte"</c>).</param>
/// <param name="Status">The evaluation verdict: Met, AtRisk, or Breached.</param>
/// <param name="ErrorBudgetRemainingPercent">
/// Remaining error budget as a percentage (0-100). <c>100</c> when Met, <c>0</c> when Breached,
/// interpolated when AtRisk.
/// </param>
/// <param name="SparklineQuery">
/// The PromQL query the frontend can use to render an inline sparkline chart.
/// Typically the same as the configured <c>ValueQuery</c>.
/// </param>
public record SloStatus(
    string Id,
    string Name,
    string Description,
    double Target,
    double CurrentValue,
    string Unit,
    string Comparator,
    SloVerdict Status,
    double ErrorBudgetRemainingPercent,
    string SparklineQuery);

/// <summary>
/// The evaluation verdict for a single SLO target.
/// </summary>
public enum SloVerdict
{
    /// <summary>The current value satisfies the target and is within safe operating range.</summary>
    Met,

    /// <summary>The current value satisfies the target but exceeds the warning threshold.</summary>
    AtRisk,

    /// <summary>The current value violates the target threshold.</summary>
    Breached
}
