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
}
