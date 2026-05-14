using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for conversation-level aggregates — the "executive dashboard"
/// metrics. Tracks conversation duration, turn count, and subagent spawns.
/// </summary>
/// <remarks>
/// Recorded by the agent orchestration loop when a conversation ends.
/// </remarks>
public static class OrchestrationMetrics
{
    /// <summary>End-to-end conversation duration. Tags: agent.name.</summary>
    public static Histogram<double> ConversationDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(OrchestrationConventions.ConversationDuration, "{ms}", "Conversation duration");

    /// <summary>Turn count distribution per conversation. Tags: agent.name.</summary>
    public static Histogram<int> TurnsPerConversation { get; } =
        AppInstrument.Meter.CreateHistogram<int>(OrchestrationConventions.TurnsPerConversation, "{turn}", "Turns per conversation");

    /// <summary>Subagent spawn count. Tags: agent.name, agent.parent_agent.name.</summary>
    public static Counter<long> SubagentSpawns { get; } =
        AppInstrument.Meter.CreateCounter<long>(OrchestrationConventions.SubagentSpawns, "{spawn}", "Subagent spawn count");

    /// <summary>Tool calls per session. Tags: agent.name, agent.tool.name.</summary>
    public static Counter<long> ToolCalls { get; } =
        AppInstrument.Meter.CreateCounter<long>(OrchestrationConventions.ToolCallCount, "{call}", "Tool calls per session");

    /// <summary>Per-turn wall-clock duration. Tags: agent.name.</summary>
    public static Histogram<double> TurnDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(OrchestrationConventions.TurnDuration, "{ms}", "Per-turn execution duration");

    /// <summary>Total turns executed. Tags: agent.name.</summary>
    public static Counter<long> TurnsTotal { get; } =
        AppInstrument.Meter.CreateCounter<long>(OrchestrationConventions.TurnsTotal, "{turn}", "Total turns executed");

    /// <summary>Turns that ended with an error. Tags: agent.name.</summary>
    public static Counter<long> TurnErrors { get; } =
        AppInstrument.Meter.CreateCounter<long>(OrchestrationConventions.TurnErrors, "{turn}", "Turn errors");
}
