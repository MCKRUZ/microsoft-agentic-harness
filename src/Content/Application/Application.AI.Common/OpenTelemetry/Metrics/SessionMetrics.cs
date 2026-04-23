using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking agent session lifecycle and health.
/// Health score gauge is registered via callback in the orchestration layer.
/// </summary>
public static class SessionMetrics
{
    /// <summary>Currently active sessions. Tags: agent.name.</summary>
    public static UpDownCounter<int> ActiveSessions { get; } =
        AppInstrument.Meter.CreateUpDownCounter<int>(SessionConventions.Active, "{session}", "Currently active agent sessions");

    /// <summary>Session health score metric name (registered as ObservableGauge via callback).</summary>
    public static string HealthScoreName => SessionConventions.HealthScore;
}
