using Domain.Common.Config.AI;
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
    /// <example>https://my-project.services.ai.azure.com/api/projects/my-project</example>
    public string ProjectEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bare AI Foundry resource endpoint — the same resource
    /// <see cref="ProjectEndpoint"/> targets, without the Project-scoped path segment.
    /// </summary>
    /// <remarks>
    /// Used only by <see cref="AIAgentFrameworkClientType.FoundryDirectResponses"/> to bypass
    /// Project-scoped routing, which measured ~15x higher per-call latency than calling the same
    /// model directly through this endpoint (issue #382) — Project-scoped routing adds a real
    /// orchestration hop, not a client-framework artifact. Independent of
    /// <see cref="ProjectEndpoint"/>/<see cref="IsConfigured"/>: a consumer may configure this
    /// without ever configuring the Project-scoped path, or both side by side.
    /// </remarks>
    /// <example>https://my-project.services.ai.azure.com</example>
    public string ResourceEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Entra ID credential configuration for authenticating with AI Foundry.
    /// Shared by both <see cref="ProjectEndpoint"/> and <see cref="ResourceEndpoint"/> routing —
    /// same resource, same tenant, same credential either way.
    /// </summary>
    public EntraCredentialConfig Entra { get; set; } = new();

    /// <summary>
    /// Whether AI Foundry is configured with a valid project endpoint.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectEndpoint);

    /// <summary>
    /// Whether the bare resource endpoint is configured for direct (non-Project-scoped) routing.
    /// </summary>
    public bool IsDirectResponsesConfigured => !string.IsNullOrWhiteSpace(ResourceEndpoint);
}
