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
/// ├── Exporters    — Multi-backend export targets (OTLP, Azure Monitor, Prometheus)
/// └── LlmPricing   — Per-model token pricing for cost estimation
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
}
