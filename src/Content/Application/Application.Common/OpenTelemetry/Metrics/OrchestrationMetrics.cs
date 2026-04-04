using System.Diagnostics.Metrics;
using Application.Common.OpenTelemetry.Conventions;
using Application.Common.OpenTelemetry.Instruments;

namespace Application.Common.OpenTelemetry.Metrics;

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
        AgenticHarnessInstrument.Meter.CreateHistogram<double>(AgenticSemanticConventions.Orchestration.ConversationDuration, "ms", "Conversation duration");

    /// <summary>Turn count distribution per conversation. Tags: agent.name.</summary>
    public static Histogram<int> TurnsPerConversation { get; } =
        AgenticHarnessInstrument.Meter.CreateHistogram<int>(AgenticSemanticConventions.Orchestration.TurnsPerConversation, "{turn}", "Turns per conversation");

    /// <summary>Subagent spawn count. Tags: agent.name, agent.parent_agent.name.</summary>
    public static Counter<long> SubagentSpawns { get; } =
        AgenticHarnessInstrument.Meter.CreateCounter<long>(AgenticSemanticConventions.Orchestration.SubagentSpawns, "{spawn}", "Subagent spawn count");
}
