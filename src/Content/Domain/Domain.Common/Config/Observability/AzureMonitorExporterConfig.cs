namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for the Azure Monitor (Application Insights) exporter.
/// </summary>
public class AzureMonitorExporterConfig
{
    /// <summary>
    /// Gets or sets whether the Azure Monitor exporter is enabled.
    /// </summary>
    /// <value>Default: false (requires a connection string to be useful).</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Application Insights connection string.
    /// Typically loaded from environment variable <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>
    /// or Azure Key Vault in production.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets whether a host whose platform supplies a connection string turns the exporter on by itself.
    /// </summary>
    /// <value>Default: <see langword="true"/>.</value>
    /// <remarks>
    /// Only FoundryHost reads this. Foundry injects <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> into every hosted
    /// container, and FoundryHost turns the exporter on when it is present, so a deployer who has not chosen Azure
    /// Monitor still exports the harness's traces and metrics to that resource. Set this to <see langword="false"/>, in
    /// appsettings, User Secrets or the environment, to leave <see cref="Enabled"/> to the deployer's own setting. Every
    /// other host ignores it and needs <see cref="Enabled"/> set explicitly (#904).
    /// </remarks>
    public bool EnableFromHostConnectionString { get; set; } = true;
}
