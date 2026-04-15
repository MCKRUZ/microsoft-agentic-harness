namespace Presentation.AgentHub.Models;

/// <summary>
/// CORS sub-configuration for the AgentHub host.
/// Bound from <c>AppConfig:AgentHub:Cors</c> in appsettings.json.
/// </summary>
public sealed record AgentHubCorsConfig
{
    /// <summary>
    /// Origins allowed to make cross-origin requests to this host.
    /// In development, always include <c>http://localhost:5173</c>.
    /// </summary>
    public string[] AllowedOrigins { get; init; } = [];
}
