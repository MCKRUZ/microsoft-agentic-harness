using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OpenTelemetry metric instruments for drift detection.
/// </summary>
public static class DriftMetrics
{
    /// <summary>Drift evaluations completed. Tags: scope, severity.</summary>
    public static Counter<long> Evaluations { get; } =
        AppInstrument.Meter.CreateCounter<long>(DriftConventions.Evaluations, "{evaluation}", "Drift evaluations completed");

    /// <summary>Drift-triggered escalations.</summary>
    public static Counter<long> EscalationsTriggered { get; } =
        AppInstrument.Meter.CreateCounter<long>(DriftConventions.EscalationsTriggered, "{escalation}", "Drift-triggered escalations");

    /// <summary>Drift baselines updated.</summary>
    public static Counter<long> BaselinesUpdated { get; } =
        AppInstrument.Meter.CreateCounter<long>(DriftConventions.BaselinesUpdated, "{baseline}", "Drift baselines updated");

    /// <summary>Drift evaluation duration in milliseconds.</summary>
    public static Histogram<double> EvaluationDurationMs { get; } =
        AppInstrument.Meter.CreateHistogram<double>(DriftConventions.EvaluationDurationMs, "ms", "Drift evaluation duration");
}
