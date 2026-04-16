namespace Presentation.AgentHub.Config;

/// <summary>
/// Configuration for the AgentHub presentation host.
/// Bound from <c>AppConfig:AgentHub</c> in appsettings.json.
/// </summary>
public sealed record AgentHubConfig
{
    /// <summary>File system path where conversation records are persisted.</summary>
    public string ConversationsPath { get; init; } = "./conversations";

    /// <summary>Name of the default agent used when no agent is specified.</summary>
    public string DefaultAgentName { get; init; } = string.Empty;

    /// <summary>Maximum number of conversation messages dispatched to the agent per turn.</summary>
    public int MaxHistoryMessages { get; init; } = 20;

    /// <summary>CORS configuration for this host.</summary>
    public AgentHubCorsConfig Cors { get; init; } = new();
}
