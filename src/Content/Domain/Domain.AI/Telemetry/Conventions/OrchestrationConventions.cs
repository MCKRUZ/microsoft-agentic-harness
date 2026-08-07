namespace Domain.AI.Telemetry.Conventions;

/// <summary>Orchestration-level telemetry attributes and metric names.</summary>
public static class OrchestrationConventions
{
    public const string TurnCount = "agent.orchestration.turn_count";
    public const string SubagentCount = "agent.orchestration.subagent_count";
    public const string ToolCallCount = "agent.orchestration.tool_call_count";
    public const string ConversationDuration = "agent.orchestration.conversation_duration";
    public const string TurnsPerConversation = "agent.orchestration.turns_per_conversation";
    public const string SubagentSpawns = "agent.orchestration.subagent_spawns";

    /// <summary>Per-turn wall-clock duration in milliseconds.</summary>
    public const string TurnDuration = "agent.orchestration.turn_duration";
    /// <summary>Total turns executed across all sessions.</summary>
    public const string TurnsTotal = "agent.orchestration.turns_total";
    /// <summary>Turns that ended with an error.</summary>
    public const string TurnErrors = "agent.orchestration.turn_errors";

    /// <summary>Conversations stopped because they exhausted their lifetime token budget.</summary>
    public const string ConversationsBudgetStopped = "agent.orchestration.conversations_budget_stopped";

    /// <summary>
    /// Units of agent work executing right now — a bundle run or an AG-UI run, counted up when it
    /// starts and down when it finishes.
    /// </summary>
    /// <remarks>
    /// This and <see cref="ConnectionsActive"/> replace a single <c>agent.session.active</c>, which
    /// three transports incremented while meaning three different things: runs in flight, live
    /// connections, and — from AG-UI, which had no decrement it could reach — conversations ever
    /// started. Each was defensible on its own; added together on one instrument they were not a
    /// quantity (issue #289).
    /// </remarks>
    public const string RunsActive = "agent.orchestration.runs_active";

    /// <summary>
    /// Interactive connections currently attached to a conversation. Hub-only: a stateless request and
    /// a background run have no connection to count.
    /// </summary>
    public const string ConnectionsActive = "agent.orchestration.connections_active";
}
