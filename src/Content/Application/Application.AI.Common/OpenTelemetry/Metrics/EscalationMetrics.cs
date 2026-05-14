using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking escalation request lifecycle.
/// Records request volume, resolution outcomes, latency distributions,
/// and pending escalation depth.
/// </summary>
/// <remarks>
/// Recorded by <c>DefaultEscalationService</c> (Section 08) on request creation,
/// resolution, timeout, and per-approver decision submission.
/// </remarks>
public static class EscalationMetrics
{
    /// <summary>Escalation requests created. Tags: agent_id, tool, priority.</summary>
    public static Counter<long> Requests { get; } =
        AppInstrument.Meter.CreateCounter<long>(EscalationConventions.Requests, "{request}", "Escalation requests created");

    /// <summary>Escalation resolutions. Tags: resolution_type, priority.</summary>
    public static Counter<long> Resolutions { get; } =
        AppInstrument.Meter.CreateCounter<long>(EscalationConventions.Resolutions, "{resolution}", "Escalation resolutions");

    /// <summary>Escalation request-to-resolution duration. Tags: priority.</summary>
    public static Histogram<double> DurationMs { get; } =
        AppInstrument.Meter.CreateHistogram<double>(EscalationConventions.DurationMs, "ms", "Escalation request-to-resolution duration");

    /// <summary>Escalation timeout events. Tags: priority.</summary>
    public static Counter<long> Timeouts { get; } =
        AppInstrument.Meter.CreateCounter<long>(EscalationConventions.Timeouts, "{timeout}", "Escalation timeout events");

    /// <summary>Currently pending escalations (inc on request, dec on resolution).</summary>
    public static UpDownCounter<long> Pending { get; } =
        AppInstrument.Meter.CreateUpDownCounter<long>(EscalationConventions.Pending, "{escalation}", "Currently pending escalations");

    /// <summary>Per-approver response latency. Tags: approver.</summary>
    public static Histogram<double> ApproverResponseMs { get; } =
        AppInstrument.Meter.CreateHistogram<double>(EscalationConventions.ApproverResponseMs, "ms", "Per-approver response latency");
}
