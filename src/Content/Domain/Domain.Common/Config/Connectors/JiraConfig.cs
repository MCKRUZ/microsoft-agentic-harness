namespace Domain.Common.Config.Connectors;

/// <summary>
/// Configuration for Jira integration.
/// Provides connection settings for the Jira REST API v3,
/// enabling issue management operations through the connector system.
/// </summary>
/// <remarks>
/// Uses Atlassian Cloud API token authentication (email + API token).
/// Generate an API token from your Atlassian account settings.
/// </remarks>
public class JiraConfig
{
    /// <summary>
    /// Jira base URL.
    /// Example: "https://mycompany.atlassian.net"
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// User email for API authentication.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// API Token for authentication.
    /// Store in User Secrets (dev) or Azure Key Vault (prod) — never in appsettings.json.
    /// </summary>
    public string? ApiToken { get; init; }

    /// <summary>
    /// Default project key for operations.
    /// </summary>
    public string? DefaultProject { get; init; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether this configuration is valid and complete for use.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(ApiToken);
}
