namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for the Prometheus metrics exporter.
/// Exposes a scrape endpoint that Prometheus can poll for metrics.
/// </summary>
public class PrometheusExporterConfig
{
    /// <summary>
    /// Gets or sets whether the Prometheus exporter is enabled.
    /// </summary>
    /// <value>Default: false.</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the scrape endpoint path.
    /// </summary>
    /// <value>Default: /metrics.</value>
    public string ScrapeEndpoint { get; set; } = "/metrics";
}
