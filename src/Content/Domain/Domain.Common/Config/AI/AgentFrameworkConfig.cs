namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the AI agent framework provider and default deployment settings.
/// Bound from <c>AppConfig:AI:AgentFramework</c> in appsettings.json.
/// </summary>
public class AgentFrameworkConfig
{
    /// <summary>
    /// Gets or sets the provider endpoint URL.
    /// For Azure OpenAI: <c>https://your-resource.openai.azure.com/</c>.
    /// For OpenAI: leave empty (uses default endpoint).
    /// Store in User Secrets or Key Vault — never in appsettings.json.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the API key for the provider.
    /// Store in User Secrets or Key Vault — never in appsettings.json.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the default deployment/model name used when no override is specified.
    /// </summary>
    public string DefaultDeployment { get; set; } = "default";

    /// <summary>
    /// Gets or sets the default AI framework client type.
    /// Determines which provider is used when no override is specified per-skill or per-agent.
    /// </summary>
    public AIAgentFrameworkClientType ClientType { get; set; } = AIAgentFrameworkClientType.AzureOpenAI;

    /// <summary>
    /// Returns true when minimum configuration is present to create a chat client.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
