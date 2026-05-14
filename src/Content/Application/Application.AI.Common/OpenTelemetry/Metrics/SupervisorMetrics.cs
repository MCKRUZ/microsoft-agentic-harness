using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for supervisor delegation tracking,
/// autonomy tier violations, and capability match scoring.
/// </summary>
public static class SupervisorMetrics
{
    /// <summary>Total delegations. Tags: supervisor_id, delegate_agent_id, outcome.</summary>
    public static Counter<long> DelegationsTotal { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            SupervisorConventions.DelegationsTotal, "{delegation}", "Total supervisor delegations");

    /// <summary>Delegation execution duration in milliseconds.</summary>
    public static Histogram<double> DelegationDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(
            SupervisorConventions.DelegationDuration, "ms", "Delegation execution duration");

    /// <summary>Autonomy tier exceeded events. Tags: agent_id, attempted_action, current_tier.</summary>
    public static Counter<long> AutonomyExceededTotal { get; } =
        AppInstrument.Meter.CreateCounter<long>(
            SupervisorConventions.AutonomyExceededTotal, "{exceeded}", "Autonomy tier exceeded events");

    /// <summary>Capability match selection scores for observability.</summary>
    public static Histogram<double> SelectionScore { get; } =
        AppInstrument.Meter.CreateHistogram<double>(
            SupervisorConventions.SelectionScore, "{score}", "Capability match selection scores");
}
