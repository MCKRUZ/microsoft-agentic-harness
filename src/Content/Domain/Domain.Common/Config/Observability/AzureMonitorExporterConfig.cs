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
}
