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

    /// <summary>Conversations stopped because they exhausted their lifetime token budget. Tags: agent.name.</summary>
    public static Counter<long> ConversationsBudgetStopped { get; } =
        AppInstrument.Meter.CreateCounter<long>(OrchestrationConventions.ConversationsBudgetStopped, "{conversation}", "Conversations stopped by lifetime token budget");

    /// <summary>
    /// Runs executing right now, counted up at the start of a run and down in its finally. Tags:
    /// agent.name.
    /// </summary>
    /// <remarks>
    /// Both transports that produce a bounded run report here — the bundle handler and AG-UI — because
    /// they are asking the same question, and summing them answers "how much agent work is in flight".
    /// What must not join them is the hub's count of live connections, which is
    /// <see cref="ConnectionsActive"/>. See <see cref="OrchestrationConventions.RunsActive"/> for why
    /// the single gauge these replace could not be read as a number.
    /// </remarks>
    public static UpDownCounter<int> RunsActive { get; } =
        AppInstrument.Meter.CreateUpDownCounter<int>(OrchestrationConventions.RunsActive, "{run}", "Runs currently executing");

    /// <summary>
    /// Interactive connections currently attached to a conversation. Tags: agent.name.
    /// </summary>
    /// <remarks>
    /// Hub-only, and deliberately not a count of conversations: two connections watching one
    /// conversation are two, and a conversation nobody is watching is none. Its decrements live
    /// wherever a connection stops being attached — disconnect, switching to another conversation, and
    /// the idle sweep.
    /// </remarks>
    public static UpDownCounter<int> ConnectionsActive { get; } =
        AppInstrument.Meter.CreateUpDownCounter<int>(OrchestrationConventions.ConnectionsActive, "{connection}", "Interactive connections attached to a conversation");
}
