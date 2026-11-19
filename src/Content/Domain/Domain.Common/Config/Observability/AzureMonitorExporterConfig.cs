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
    /// Gets or sets the Application Insights connection string. The exporter sends only when this is set and
    /// <see cref="Enabled"/> is <see langword="true"/>; it does not fall back to
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>. Load it from Azure Key Vault in production. By default
    /// FoundryHost maps that variable onto this setting, unless <see cref="EnableFromHostConnectionString"/> is
    /// <see langword="false"/>; no other host reads it.
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
    /// Set this to <see langword="false"/> and FoundryHost removes the variable before anything reads it: nothing is
    /// exported to Foundry's resource, and
    /// <see cref="Enabled"/> and <see cref="ConnectionString"/> are the deployer's own. Every other host ignores it
    /// (#904). Where it can be set, and how each source spells it (Key Vault and environment variables need a different
    /// form of the name), is described once, in the security guide: <c>documentation/security/08-data-protection.html</c>,
    /// section <c>#foundry-telemetry-opt-out</c>.
    /// </para>
    /// </remarks>
    public bool EnableFromHostConnectionString { get; set; } = true;
}
