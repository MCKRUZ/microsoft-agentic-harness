namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for multi-backend telemetry export. Enables fan-out from
/// a single OTLP source to multiple observability backends simultaneously.
/// </summary>
/// <remarks>
/// <para>
/// Each exporter can be independently enabled/disabled. The application exports
/// telemetry once via the OTel SDK; the multi-exporter configurator wires up
/// all enabled targets.
/// </para>
/// </remarks>
public class ExportersConfig
{
    /// <summary>
    /// Gets or sets the OTLP/gRPC exporter configuration (e.g., Jaeger, Tempo).
    /// </summary>
    public OtlpExporterConfig Otlp { get; set; } = new();

    /// <summary>
    /// Gets or sets the Azure Monitor exporter configuration.
    /// </summary>
    public AzureMonitorExporterConfig AzureMonitor { get; set; } = new();

    /// <summary>
    /// Gets or sets the Prometheus metrics exporter configuration.
    /// </summary>
    public PrometheusExporterConfig Prometheus { get; set; } = new();
}
