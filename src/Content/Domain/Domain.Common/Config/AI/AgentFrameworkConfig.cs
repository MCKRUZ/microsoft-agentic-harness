namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the AI agent framework provider and default deployment settings.
/// Bound from <c>AppConfig:AI:AgentFramework</c> in appsettings.json.
/// </summary>
public class AgentFrameworkConfig
{
    /// <summary>
    /// Gets or sets the default deployment/model name used when no override is specified.
    /// </summary>
    public string DefaultDeployment { get; set; } = "default";

    /// <summary>
    /// Gets or sets the default AI framework client type.
    /// Determines which provider is used when no override is specified per-skill or per-agent.
    /// </summary>
    public AIAgentFrameworkClientType ClientType { get; set; } = AIAgentFrameworkClientType.AzureOpenAI;
}
