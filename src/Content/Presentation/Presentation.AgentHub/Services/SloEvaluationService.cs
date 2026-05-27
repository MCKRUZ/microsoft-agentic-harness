using Application.Common.Models;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Interfaces;

namespace Presentation.AgentHub.Services;

/// <summary>
/// Evaluates configured SLO targets by querying Prometheus for current metric values,
/// comparing against thresholds, and producing verdicts with error-budget status.
/// </summary>
/// <remarks>
/// <para>
/// Each target's <see cref="SloTargetConfig.ValueQuery"/> is executed as a Prometheus
/// instant query. The first scalar value from the response is compared against the
/// target threshold using the configured comparator to produce one of three verdicts:
/// </para>
/// <list type="bullet">
///   <item><description><b>Met</b> -- value satisfies the target and is within the warning threshold.</description></item>
///   <item><description><b>AtRisk</b> -- value satisfies the target but exceeds the warning threshold.</description></item>
///   <item><description><b>Breached</b> -- value violates the target threshold.</description></item>
/// </list>
/// <para>
/// If Prometheus is unreachable or the query fails, the target is reported as
/// <see cref="SloVerdict.Breached"/> with <c>CurrentValue = -1</c>. This fail-closed
/// design ensures operational visibility even during infrastructure degradation.
/// </para>
/// </remarks>
public sealed class SloEvaluationService : ISloEvaluator
{
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly IPrometheusQueryService _prometheus;
    private readonly ILogger<SloEvaluationService> _logger;

    /// <summary>Initialises the service with its dependencies.</summary>
    /// <param name="configMonitor">Configuration monitor providing access to <see cref="SloConfig"/>.</param>
    /// <param name="prometheus">Prometheus query service for executing PromQL instant queries.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public SloEvaluationService(
        IOptionsMonitor<AppConfig> configMonitor,
        IPrometheusQueryService prometheus,
        ILogger<SloEvaluationService> logger)
    {
        _configMonitor = configMonitor;
        _prometheus = prometheus;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SloStatus>> EvaluateAllAsync(CancellationToken ct = default)
    {
        var sloConfig = _configMonitor.CurrentValue.Observability.Slo;

        if (!sloConfig.Enabled || sloConfig.Targets.Count == 0)
            return [];

        var tasks = sloConfig.Targets.Select(target => EvaluateTargetAsync(target, ct));
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Evaluates a single SLO target by querying Prometheus and comparing the result
    /// against the configured thresholds.
    /// </summary>
    private async Task<SloStatus> EvaluateTargetAsync(SloTargetConfig target, CancellationToken ct)
    {
        double currentValue;
        try
        {
            var response = await _prometheus.QueryInstantAsync(target.ValueQuery, cancellationToken: ct);

            if (!response.Success || response.Series.Count == 0)
            {
                _logger.LogWarning(
                    "SLO {SloId}: Prometheus query returned no data. Error: {Error}",
                    target.Id, response.Error ?? "empty result");
                return BuildBreachedStatus(target, currentValue: -1);
            }

            var dataPoints = response.Series[0].DataPoints;
            currentValue = dataPoints.Count > 0
                ? ParseValue(dataPoints[^1].Value)
                : -1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SLO {SloId}: Prometheus query failed", target.Id);
            return BuildBreachedStatus(target, currentValue: -1);
        }

        if (currentValue < 0 || double.IsNaN(currentValue) || double.IsInfinity(currentValue))
            return BuildBreachedStatus(target, currentValue);

        var verdict = DeriveVerdict(target, currentValue);
        var budgetRemaining = CalculateErrorBudgetRemaining(target, currentValue, verdict);

        return new SloStatus(
            Id: target.Id,
            Name: target.Name,
            Description: target.Description,
            Target: target.Target,
            CurrentValue: currentValue,
            Unit: target.Unit,
            Comparator: target.Comparator,
            Status: verdict,
            ErrorBudgetRemainingPercent: budgetRemaining,
            SparklineQuery: target.ValueQuery);
    }

    /// <summary>
    /// Determines the SLO verdict by comparing the current value against the target
    /// and warning thresholds using the configured comparator.
    /// </summary>
    /// <param name="target">The SLO target configuration.</param>
    /// <param name="currentValue">The current metric value from Prometheus.</param>
    /// <returns>The evaluation verdict.</returns>
    internal static SloVerdict DeriveVerdict(SloTargetConfig target, double currentValue)
    {
        if (!CompareValue(currentValue, target.Target, target.Comparator))
            return SloVerdict.Breached;

        if (!CompareValue(currentValue, target.WarningThreshold, target.Comparator))
            return SloVerdict.AtRisk;

        return SloVerdict.Met;
    }

    /// <summary>
    /// Evaluates whether <paramref name="value"/> satisfies the <paramref name="comparator"/>
    /// against <paramref name="threshold"/>.
    /// </summary>
    private static bool CompareValue(double value, double threshold, string comparator) =>
        comparator switch
        {
            "lt" => value < threshold,
            "lte" => value <= threshold,
            "gt" => value > threshold,
            "gte" => value >= threshold,
            _ => value < threshold // default to "lt" for safety
        };

    /// <summary>
    /// Calculates error budget remaining as a percentage (0-100).
    /// Met = 100%, Breached = 0%, AtRisk = interpolated between warning and target.
    /// </summary>
    private static double CalculateErrorBudgetRemaining(
        SloTargetConfig target, double currentValue, SloVerdict verdict) =>
        verdict switch
        {
            SloVerdict.Met => 100.0,
            SloVerdict.Breached => 0.0,
            SloVerdict.AtRisk => InterpolateErrorBudget(target, currentValue),
            _ => 0.0
        };

    /// <summary>
    /// Linearly interpolates the error budget between the warning threshold (100%)
    /// and the target threshold (0%) based on the current value.
    /// </summary>
    private static double InterpolateErrorBudget(SloTargetConfig target, double currentValue)
    {
        var range = Math.Abs(target.Target - target.WarningThreshold);
        if (range == 0)
            return 50.0;

        var distanceFromTarget = Math.Abs(target.Target - currentValue);
        return Math.Clamp(distanceFromTarget / range * 100.0, 0.0, 100.0);
    }

    /// <summary>
    /// Creates a <see cref="SloStatus"/> with <see cref="SloVerdict.Breached"/> verdict.
    /// Used when Prometheus is unreachable or the query returns unparseable data.
    /// </summary>
    private static SloStatus BuildBreachedStatus(SloTargetConfig target, double currentValue) =>
        new(
            Id: target.Id,
            Name: target.Name,
            Description: target.Description,
            Target: target.Target,
            CurrentValue: currentValue,
            Unit: target.Unit,
            Comparator: target.Comparator,
            Status: SloVerdict.Breached,
            ErrorBudgetRemainingPercent: 0.0,
            SparklineQuery: target.ValueQuery);

    /// <summary>
    /// Parses a Prometheus metric value string to a double.
    /// Returns <see cref="double.NaN"/> for unparseable values.
    /// </summary>
    private static double ParseValue(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : double.NaN;
}
