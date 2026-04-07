namespace Domain.Common.Config.Connectors;

/// <summary>
/// Configuration for GitHub integration.
/// Provides connection settings for the GitHub REST API,
/// enabling issue and repository management operations through the connector system.
/// </summary>
/// <remarks>
/// <para>
/// Required GitHub PAT scopes:
/// <list type="bullet">
///   <item><description>repo (full control of private repositories)</description></item>
///   <item><description>read:org (read organization membership)</description></item>
/// </list>
/// </para>
/// <para>
/// For GitHub Enterprise Server, override <see cref="BaseUrl"/> with your instance URL.
/// </para>
/// </remarks>
public class GitHubConfig
{
    /// <summary>
    /// Personal Access Token (PAT) for authentication.
    /// Store in User Secrets (dev) or Azure Key Vault (prod) — never in appsettings.json.
    /// </summary>
    public string? AccessToken { get; init; }

    /// <summary>
    /// GitHub API base URL. Override for GitHub Enterprise Server.
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.github.com";

    /// <summary>
    /// Default owner/organization for repository operations.
    /// </summary>
    public string? DefaultOwner { get; init; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether this configuration is valid and complete for use.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessToken);
}
