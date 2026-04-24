namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for the observability pipeline including tail-based sampling,
/// PII filtering, rate limiting, and multi-backend export.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.Observability
/// ├── Sampling     — Tail-based sampling policies (keep errors, slow, agent spans)
/// ├── PiiFiltering — Attribute scrubbing rules (delete, hash)
/// ├── RateLimiting — Span throughput throttling
/// ├── Exporters       — Multi-backend export targets (OTLP, Azure Monitor, Prometheus)
/// ├── LlmPricing      — Per-model token pricing for cost estimation
/// └── BudgetTracking  — Cost budget thresholds and alerting state machine
/// </code>
/// </para>
/// <para>
/// These processors run in the OTel SDK pipeline at Order 300 (Finalization),
/// after all sources and domain-specific processors have been registered.
/// </para>
/// </remarks>
public class ObservabilityConfig
{
    /// <summary>
    /// Gets or sets the tail-based sampling configuration.
    /// </summary>
    public SamplingConfig Sampling { get; set; } = new();

    /// <summary>
    /// Gets or sets the PII filtering configuration.
    /// </summary>
    public PiiFilteringConfig PiiFiltering { get; set; } = new();

    /// <summary>
    /// Gets or sets the rate limiting configuration.
    /// </summary>
    public RateLimitingConfig RateLimiting { get; set; } = new();

    /// <summary>
    /// Gets or sets the multi-backend exporter configuration.
    /// </summary>
    public ExportersConfig Exporters { get; set; } = new();

    /// <summary>
    /// Gets or sets the LLM token pricing configuration for cost estimation.
    /// </summary>
    public LlmPricingConfig LlmPricing { get; set; } = new();

    /// <summary>
    /// Gets or sets the cost budget tracking configuration for automated alerting.
    /// </summary>
    public BudgetTrackingConfig BudgetTracking { get; set; } = new();

    /// <summary>
    /// Gets or sets the PostgreSQL connection string for persisting observability data
    /// (sessions, messages, tool executions, audit log) for Grafana dashboard queries.
    /// When null or empty, persistence is disabled and <c>NullObservabilityStore</c> is used.
    /// </summary>
    /// <value>Default: <c>null</c> (persistence disabled).</value>
    public string? PostgresConnectionString { get; set; }

    /// <summary>
    /// Gets or sets whether sensitive telemetry data (e.g., GenAI prompt/completion content)
    /// is recorded in traces. Controls the <c>Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive</c>
    /// AppContext switch. When <c>false</c>, only non-sensitive GenAI metadata (model, token counts)
    /// is captured; prompt and completion text is omitted.
    /// </summary>
    /// <value>Default: <c>false</c>. Must be explicitly opted into — never enable in production
    /// unless PII filtering and data retention policies are in place.</value>
    public bool EnableSensitiveTelemetry { get; set; }

    /// <summary>
    /// Gets or sets the list of project assembly names that should use
    /// web-specific OpenTelemetry configuration (ASP.NET Core instrumentation).
    /// Projects not in this list use desktop/standalone OTel configuration.
    /// </summary>
    /// <value>Default: ["Infrastructure.AI.MCPServer"].</value>
    public List<string> WebTelemetryProjects { get; set; } =
    [
        "Infrastructure.AI.MCPServer"
    ];
}
