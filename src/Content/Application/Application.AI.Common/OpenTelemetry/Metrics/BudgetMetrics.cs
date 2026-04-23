using Domain.AI.Telemetry.Conventions;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// Cost budget metric name constants. All budget metrics are ObservableGauges
/// registered via callbacks in <c>BudgetTrackingService</c> which owns the
/// spend state. This class exposes the metric names for consistent reference.
/// </summary>
public static class BudgetMetrics
{
    /// <summary>Current spend in USD for the active period.</summary>
    public static string CurrentSpendName => BudgetConventions.CurrentSpend;
    /// <summary>Budget status: 0=clear, 1=warning, 2=critical.</summary>
    public static string StatusName => BudgetConventions.Status;
    /// <summary>Warning threshold in USD.</summary>
    public static string ThresholdWarningName => BudgetConventions.ThresholdWarning;
    /// <summary>Critical threshold in USD.</summary>
    public static string ThresholdCriticalName => BudgetConventions.ThresholdCritical;
}
