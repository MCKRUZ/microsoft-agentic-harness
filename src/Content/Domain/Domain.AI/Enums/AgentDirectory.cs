namespace Domain.AI.Enums;

/// <summary>
/// AI-specific directory types used by the agentic harness.
/// </summary>
public enum AgentDirectory
{
    /// <summary>Skill definition files.</summary>
    Skills = 0,

    /// <summary>Agent manifest files.</summary>
    Manifests,

    /// <summary>MCP server configuration and state.</summary>
    Mcp
}
