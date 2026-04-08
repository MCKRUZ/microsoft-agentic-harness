using Domain.Common.Config.Azure;

namespace Domain.Common.Config.AI.AIFoundry;

/// <summary>
/// Configuration for Azure AI Foundry (formerly Azure AI Studio) persistent agents.
/// Bound from <c>AppConfig:AI:AIFoundry</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// AI Foundry persistent agents are server-side agent instances managed by Azure.
/// They persist across sessions and can be shared across applications.
/// </para>
/// <para>
/// Authentication uses Entra ID (Azure AD) credentials — either <c>DefaultAzureCredential</c>
/// (recommended for development) or explicit client secret/certificate credentials.
/// </para>
/// </remarks>
public class AIFoundryConfig
{
    /// <summary>
    /// Gets or sets the AI Foundry project endpoint URL.
    /// </summary>
    /// <example>https://my-project.services.ai.azure.com</example>
    public string ProjectEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Entra ID credential configuration for authenticating with AI Foundry.
    /// </summary>
    public EntraCredentialConfig Entra { get; set; } = new();

    /// <summary>
    /// Whether AI Foundry is configured with a valid project endpoint.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectEndpoint);
}
