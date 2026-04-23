namespace Domain.AI.Telemetry.Conventions;

/// <summary>Agent and conversation telemetry attributes.</summary>
public static class AgentConventions
{
    public const string Name = "agent.name";
    public const string ParentName = "agent.parent_agent.name";
    public const string ConversationId = "agent.conversation.id";
    public const string TurnIndex = "agent.turn.index";
    public const string TurnRole = "agent.turn.role";
    public const string Phase = "agent.phase";

    /// <summary>Gen AI semantic convention attributes.</summary>
    public const string GenAiSystem = "gen_ai.system";
    public const string GenAiSystemSemanticKernel = "semantic-kernel";
    public const string GenAiSystemExtensionsAI = "microsoft.extensions.ai";
    public const string GenAiSystemAgentsAI = "microsoft.agents.ai";

    /// <summary>Agent config info metric (always 1, carries config labels). Tags: agent.name, model, temperature, tools_count, skills_count, mcp_servers_count.</summary>
    public const string ConfigInfo = "agent.config.info";
    /// <summary>LLM model identifier for the agent.</summary>
    public const string ConfigModel = "agent.config.model";
    /// <summary>Temperature setting for the agent.</summary>
    public const string ConfigTemperature = "agent.config.temperature";
    /// <summary>Number of tools registered for the agent.</summary>
    public const string ConfigToolsCount = "agent.config.tools_count";
    /// <summary>Number of skills loaded for the agent.</summary>
    public const string ConfigSkillsCount = "agent.config.skills_count";
    /// <summary>Number of MCP servers connected for the agent.</summary>
    public const string ConfigMcpServersCount = "agent.config.mcp_servers_count";
}
