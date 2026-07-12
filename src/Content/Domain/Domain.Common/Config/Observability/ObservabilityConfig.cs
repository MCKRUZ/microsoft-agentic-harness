namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for the observability pipeline including PII filtering,
/// rate limiting, and multi-backend export.
/// </summary>
/// <remarks>
/// <para>
/// Trace sampling is intentionally not configured here. Tail-based sampling is
/// performed at the OpenTelemetry Collector tier (see the observability
/// architecture guide), not in-app — the SDK exports all spans and the collector
/// decides what to keep.
/// </para>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.Observability
/// ├── PiiFiltering    — Attribute scrubbing rules (delete, hash)
/// ├── RateLimiting    — Span throughput throttling
/// ├── Exporters       — Multi-backend export targets (OTLP, Azure Monitor, Prometheus)
/// ├── LlmPricing      — Per-model token pricing for cost estimation
/// ├── BudgetTracking  — Cost budget thresholds and alerting state machine
/// └── Slo             — Service Level Objective targets and evaluation
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
    /// Gets or sets the OpenTelemetry logs-signal configuration (the
    /// <c>ILogger</c> → OTel bridge, PII scrub, and export level). OFF by default —
    /// logs stay on the local sinks until <see cref="LogsConfig.OtelExportEnabled"/>
    /// is set.
    /// </summary>
    public LogsConfig Logs { get; set; } = new();

    /// <summary>
    /// Gets or sets the LLM token pricing configuration for cost estimation.
    /// </summary>
    public LlmPricingConfig LlmPricing { get; set; } = new();

    /// <summary>
    /// Gets or sets the cost budget tracking configuration for automated alerting.
    /// </summary>
    public BudgetTrackingConfig BudgetTracking { get; set; } = new();

    /// <summary>
    /// Gets or sets the SLO (Service Level Objective) evaluation configuration.
    /// Defines operational health targets evaluated against live Prometheus metrics.
    /// </summary>
    public SloConfig Slo { get; set; } = new();

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
