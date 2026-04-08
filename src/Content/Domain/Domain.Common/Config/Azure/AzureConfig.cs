namespace Domain.Common.Config.Azure;

/// <summary>
/// Configuration for Azure platform services used by the agentic harness.
/// Each subsection is conditional — services are only activated when their
/// connection strings or endpoints are configured.
/// </summary>
/// <remarks>
/// <code>
/// AppConfig.Azure
/// ├── ApplicationInsights — Telemetry and health check publishing
/// ├── Database            — SQL, Blob Storage connections
/// ├── ADB2C               — Azure AD B2C authentication
/// ├── KeyVault             — Secret management
/// └── GraphApi             — Microsoft Graph API access
/// </code>
/// </remarks>
public class AzureConfig
{
    /// <summary>
    /// Gets or sets the Application Insights configuration for telemetry export
    /// and health check publishing.
    /// </summary>
    public ApplicationInsightsConfig ApplicationInsights { get; set; } = new();

    /// <summary>
    /// Gets or sets the Azure database services configuration including
    /// SQL Server and Blob Storage.
    /// </summary>
    public AzureDatabaseConfig Database { get; set; } = new();

    /// <summary>
    /// Gets or sets the Azure AD B2C configuration for user authentication.
    /// When <see cref="AzureADB2CConfig.Instance"/> is null or empty, B2C auth is skipped.
    /// </summary>
    public AzureADB2CConfig ADB2C { get; set; } = new();

    /// <summary>
    /// Gets or sets the Azure Key Vault configuration for secret management.
    /// </summary>
    public KeyVaultConfig KeyVault { get; set; } = new();

    /// <summary>
    /// Gets or sets the Microsoft Graph API configuration.
    /// </summary>
    public GraphApiConfig GraphApi { get; set; } = new();
}
