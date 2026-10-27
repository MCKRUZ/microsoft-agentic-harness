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
    /// Gets or sets whether a host sends its telemetry to the Application Insights resource its platform supplies.
    /// </summary>
    /// <value>Default: <see langword="true"/>.</value>
    /// <remarks>
    /// <para>
    /// Only FoundryHost reads this. Foundry injects <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> into every hosted
    /// container. By default FoundryHost turns the harness's exporter on with it, and the Foundry hosting layer exports
    /// its own traces and logs to it, so a deployer who has not chosen Azure Monitor still exports to that resource.
    /// </para>
    /// <para>
    /// Set this to <see langword="false"/>, in appsettings, User Secrets or the environment, and FoundryHost removes the
    /// variable before anything reads it: nothing is exported to Foundry's resource, and <see cref="Enabled"/> and
    /// <see cref="ConnectionString"/> are the deployer's own. Key Vault and App Configuration are read too late for this
    /// setting; the host warns when it is set only there. Every other host ignores it (#904).
    /// </para>
    /// </remarks>
    public bool EnableFromHostConnectionString { get; set; } = true;
}
