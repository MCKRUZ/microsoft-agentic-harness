using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Observability.Services;

/// <summary>
/// Tracks cumulative LLM cost spend against configured budget thresholds.
/// Implements a state machine with hysteresis to prevent alert flapping.
/// Registers ObservableGauge callbacks for Prometheus scraping.
/// </summary>
public sealed class BudgetTrackingService : IBudgetTrackingService
{
    private readonly ILogger<BudgetTrackingService> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly object _lock = new();

    private readonly Dictionary<string, PeriodState> _periods = new()
    {
        [BudgetConventions.PeriodValues.Daily] = new(),
        [BudgetConventions.PeriodValues.Weekly] = new(),
        [BudgetConventions.PeriodValues.Monthly] = new(),
    };

    public BudgetTrackingService(
        ILogger<BudgetTrackingService> logger,
        IOptionsMonitor<AppConfig> appConfig)
    {
        _logger = logger;
        _appConfig = appConfig;

        InitializePeriodBoundaries();
        RegisterGauges();

        _logger.LogInformation("Budget tracking service initialized");
    }

    /// <inheritdoc />
    public void RecordSpend(double amountUsd, string agentName)
    {
        lock (_lock)
        {
            RolloverIfNeeded();

            foreach (var (period, state) in _periods)
            {
                state.CurrentSpend += amountUsd;
                var newStatus = EvaluateStatus(period, state.CurrentSpend, state.Status);

                if (newStatus != state.Status)
                {
                    _logger.LogWarning(
                        "Budget status changed: {Period} {OldStatus} -> {NewStatus} (spend: ${Spend:F2}, agent: {Agent})",
                        period, state.Status, newStatus, state.CurrentSpend, agentName);
                    state.Status = newStatus;
                }
            }
        }
    }

    /// <inheritdoc />
    public int GetCurrentStatus(string period)
    {
        lock (_lock)
        {
            RolloverIfNeeded();
            return _periods.TryGetValue(period, out var state) ? state.Status : BudgetConventions.StatusValues.Clear;
        }
    }

    /// <inheritdoc />
    public double GetCurrentSpend(string period)
    {
        lock (_lock)
        {
            RolloverIfNeeded();
            return _periods.TryGetValue(period, out var state) ? state.CurrentSpend : 0;
        }
    }

    /// <inheritdoc />
    public double GetThreshold(string period, string level)
    {
        var config = _appConfig.CurrentValue.Observability.BudgetTracking;
        var budget = GetBudgetForPeriod(period, config);

        return level switch
        {
            "warning" => (double)budget * config.WarningThresholdPercent,
            "critical" => (double)budget * config.CriticalThresholdPercent,
            _ => 0
        };
    }

    private int EvaluateStatus(string period, double spend, int currentStatus)
    {
        var config = _appConfig.CurrentValue.Observability.BudgetTracking;
        var budget = (double)GetBudgetForPeriod(period, config);

        var warnThreshold = budget * config.WarningThresholdPercent;
        var critThreshold = budget * config.CriticalThresholdPercent;
        var warnClear = warnThreshold * config.HysteresisPercent;
        var critClear = critThreshold * config.HysteresisPercent;

        return currentStatus switch
        {
            BudgetConventions.StatusValues.Clear when spend >= critThreshold => BudgetConventions.StatusValues.Critical,
            BudgetConventions.StatusValues.Clear when spend >= warnThreshold => BudgetConventions.StatusValues.Warning,
            BudgetConventions.StatusValues.Warning when spend >= critThreshold => BudgetConventions.StatusValues.Critical,
            BudgetConventions.StatusValues.Warning when spend < warnClear => BudgetConventions.StatusValues.Clear,
            BudgetConventions.StatusValues.Critical when spend < warnClear => BudgetConventions.StatusValues.Clear,
            BudgetConventions.StatusValues.Critical when spend < critClear => BudgetConventions.StatusValues.Warning,
            _ => currentStatus
        };
    }

    private void RolloverIfNeeded()
    {
        var now = DateTime.UtcNow;

        foreach (var (period, state) in _periods)
        {
            var boundary = GetNextBoundary(period, state.PeriodStart);
            if (now < boundary) continue;

            _logger.LogInformation("Budget period rollover: {Period} (was ${Spend:F2})", period, state.CurrentSpend);
            state.CurrentSpend = 0;
            state.Status = BudgetConventions.StatusValues.Clear;
            state.PeriodStart = GetCurrentPeriodStart(period, now);
        }
    }

    private void InitializePeriodBoundaries()
    {
        var now = DateTime.UtcNow;
        _periods[BudgetConventions.PeriodValues.Daily].PeriodStart = now.Date;
        _periods[BudgetConventions.PeriodValues.Weekly].PeriodStart = GetCurrentPeriodStart(BudgetConventions.PeriodValues.Weekly, now);
        _periods[BudgetConventions.PeriodValues.Monthly].PeriodStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private void RegisterGauges()
    {
        var meter = AppInstrument.Meter;

        meter.CreateObservableGauge(
            BudgetConventions.CurrentSpend, () => ObserveSpend(), "{USD}", "Current spend in USD per budget period");

        meter.CreateObservableGauge(
            BudgetConventions.Status, () => ObserveStatus(), "{status}", "Budget status (0=clear, 1=warning, 2=critical)");

        meter.CreateObservableGauge(
            BudgetConventions.ThresholdWarning, () => ObserveThreshold("warning"), "{USD}", "Warning threshold in USD");

        meter.CreateObservableGauge(
            BudgetConventions.ThresholdCritical, () => ObserveThreshold("critical"), "{USD}", "Critical threshold in USD");
    }

    private IEnumerable<Measurement<double>> ObserveSpend()
    {
        lock (_lock)
        {
            RolloverIfNeeded();
            foreach (var (period, state) in _periods)
                yield return new Measurement<double>(state.CurrentSpend, new KeyValuePair<string, object?>(BudgetConventions.Period, period));
        }
    }

    private IEnumerable<Measurement<int>> ObserveStatus()
    {
        lock (_lock)
        {
            RolloverIfNeeded();
            foreach (var (period, state) in _periods)
                yield return new Measurement<int>(state.Status, new KeyValuePair<string, object?>(BudgetConventions.Period, period));
        }
    }

    private IEnumerable<Measurement<double>> ObserveThreshold(string level)
    {
        foreach (var period in _periods.Keys)
        {
            var threshold = GetThreshold(period, level);
            yield return new Measurement<double>(threshold, new KeyValuePair<string, object?>(BudgetConventions.Period, period));
        }
    }

    private static decimal GetBudgetForPeriod(string period, Domain.Common.Config.Observability.BudgetTrackingConfig config)
    {
        return period switch
        {
            BudgetConventions.PeriodValues.Daily => config.DailyBudgetUsd,
            BudgetConventions.PeriodValues.Weekly => config.WeeklyBudgetUsd,
            BudgetConventions.PeriodValues.Monthly => config.MonthlyBudgetUsd,
            _ => 0m
        };
    }

    private static DateTime GetCurrentPeriodStart(string period, DateTime now)
    {
        return period switch
        {
            BudgetConventions.PeriodValues.Daily => now.Date,
            BudgetConventions.PeriodValues.Weekly => now.Date.AddDays(-(int)now.DayOfWeek + (int)DayOfWeek.Monday),
            BudgetConventions.PeriodValues.Monthly => new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => now.Date
        };
    }

    private static DateTime GetNextBoundary(string period, DateTime periodStart)
    {
        return period switch
        {
            BudgetConventions.PeriodValues.Daily => periodStart.AddDays(1),
            BudgetConventions.PeriodValues.Weekly => periodStart.AddDays(7),
            BudgetConventions.PeriodValues.Monthly => periodStart.AddMonths(1),
            _ => periodStart.AddDays(1)
        };
    }

    private sealed class PeriodState
    {
        public double CurrentSpend { get; set; }
        public int Status { get; set; } = BudgetConventions.StatusValues.Clear;
        public DateTime PeriodStart { get; set; }
    }
}
