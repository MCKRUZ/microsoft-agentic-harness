namespace Domain.AI.Telemetry.Conventions;

/// <summary>Agent and conversation telemetry attributes.</summary>
public static class AgentConventions
{
    public const string Name = "agent.name";
    public const string ParentName = "agent.parent_agent.name";
    public const string ConversationId = "agent.conversation.id";
    public const string TurnIndex = "agent.turn.index";
    public const string TurnRole = "agent.turn.role";
}
