namespace Domain.Common.Config.Connectors;

/// <summary>
/// Configuration for Azure DevOps integration.
/// Provides connection settings for the Azure DevOps REST API,
/// enabling work item management operations through the connector system.
/// </summary>
/// <remarks>
/// <para>
/// Required Azure DevOps PAT scopes:
/// <list type="bullet">
///   <item><description>Code (Read &amp; Write)</description></item>
///   <item><description>Work Items (Read &amp; Write)</description></item>
///   <item><description>Build (Read &amp; Execute)</description></item>
/// </list>
/// </para>
/// <para>
/// Configuration example in appsettings.json:
/// <code>
/// "Connectors": {
///   "AzureDevOps": {
///     "OrganizationUrl": "https://dev.azure.com/myorganization",
///     "PersonalAccessToken": "-- stored in User Secrets --",
///     "DefaultProject": "MyProject"
///   }
/// }
/// </code>
/// </para>
/// </remarks>
public class AzureDevOpsConfig
{
    /// <summary>
    /// Azure DevOps organization URL.
    /// Example: "https://dev.azure.com/myorganization"
    /// </summary>
    public string? OrganizationUrl { get; init; }

    /// <summary>
    /// Personal Access Token (PAT) for authentication.
    /// Store in User Secrets (dev) or Azure Key Vault (prod) — never in appsettings.json.
    /// </summary>
    public string? PersonalAccessToken { get; init; }

    /// <summary>
    /// Default project to use when not specified in operations.
    /// </summary>
    public string? DefaultProject { get; init; }

    /// <summary>
    /// API version to use for REST calls.
    /// </summary>
    public string ApiVersion { get; init; } = "7.1-preview.3";

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether this configuration is valid and complete for use.
    /// Checks that required connection properties are present without making external calls.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(OrganizationUrl) &&
        !string.IsNullOrWhiteSpace(PersonalAccessToken);
}
