using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// Per-user activity counters for the Users observability tab.
/// Counters only — no histograms to avoid cardinality explosion from user_id dimension.
/// </summary>
public static class UserActivityMetrics
{
    /// <summary>Turns initiated per user. Tags: user.id, agent.name.</summary>
    public static Counter<long> Turns { get; } =
        AppInstrument.Meter.CreateCounter<long>(UserConventions.Turns, "{turn}", "Turns initiated per user");

    /// <summary>Tokens consumed per user. Tags: user.id, agent.name.</summary>
    public static Counter<long> TokensConsumed { get; } =
        AppInstrument.Meter.CreateCounter<long>(UserConventions.TokensConsumed, "{token}", "Tokens consumed per user");

    /// <summary>Estimated cost per user. Tags: user.id, agent.name.</summary>
    public static Counter<double> CostAccrued { get; } =
        AppInstrument.Meter.CreateCounter<double>(UserConventions.CostAccrued, "{USD}", "Estimated cost per user");

    /// <summary>Sessions started per user. Tags: user.id.</summary>
    public static Counter<long> SessionsStarted { get; } =
        AppInstrument.Meter.CreateCounter<long>(UserConventions.SessionsStarted, "{session}", "Sessions started per user");
}
