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

    /// <summary>Session cost distribution in USD. Tags: agent.name, gen_ai.request.model.</summary>
    public static Histogram<double> SessionCost { get; } =
        AppInstrument.Meter.CreateHistogram<double>(SessionConventions.SessionCost, "{usd}", "Session cost in USD");

    /// <summary>Total sessions started. Tags: agent.name.</summary>
    public static Counter<long> SessionsStarted { get; } =
        AppInstrument.Meter.CreateCounter<long>(SessionConventions.SessionsStarted, "{session}", "Sessions started");
}
