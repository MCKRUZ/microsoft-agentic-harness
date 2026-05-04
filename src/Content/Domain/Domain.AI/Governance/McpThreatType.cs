namespace Domain.AI.Governance;

/// <summary>
/// Classification of MCP tool security threats detected by the scanner.
/// </summary>
public enum McpThreatType
{
    /// <summary>Tool description contains hidden instructions for the LLM.</summary>
    ToolPoisoning,
    /// <summary>Tool name mimics a legitimate tool with subtle differences.</summary>
    Typosquatting,
    /// <summary>Hidden instructions embedded in tool schema or description.</summary>
    HiddenInstruction,
    /// <summary>Tool behavior changes after initial trust establishment.</summary>
    RugPull,
    /// <summary>Tool schema designed to extract unauthorized data.</summary>
    SchemaAbuse,
    /// <summary>Tool attempts to influence other MCP server tools.</summary>
    CrossServerAttack,
    /// <summary>Tool description contains prompt injection targeting the LLM.</summary>
    DescriptionInjection
}
