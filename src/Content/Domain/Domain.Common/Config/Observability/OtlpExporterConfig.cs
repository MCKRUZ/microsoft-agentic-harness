namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for the OTLP exporter targeting gRPC-compatible backends
/// such as Jaeger, Grafana Tempo, or another OpenTelemetry Collector.
/// </summary>
public class OtlpExporterConfig
{
    /// <summary>
    /// Gets or sets whether the OTLP exporter is enabled.
    /// </summary>
    /// <value>Default: true.</value>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the OTLP endpoint URL.
    /// </summary>
    /// <value>Default: http://localhost:4317 (standard OTLP gRPC port).</value>
    public string Endpoint { get; set; } = "http://localhost:4317";

    /// <summary>
    /// Gets or sets the export timeout.
    /// </summary>
    /// <value>Default: 30 seconds.</value>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets additional headers to include in export requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Do not put secrets directly in appsettings.json.</strong>
    /// Use environment variable references or Azure Key Vault for
    /// authentication tokens on managed backends.
    /// </para>
    /// </remarks>
    public Dictionary<string, string> Headers { get; set; } = new();
}
