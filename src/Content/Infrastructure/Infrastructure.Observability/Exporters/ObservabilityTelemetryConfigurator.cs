using Application.AI.Common.Interfaces;
using Application.Common.Interfaces.Telemetry;
using Azure.Monitor.OpenTelemetry.Exporter;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Infrastructure.Observability.Exporters;

/// <summary>
/// Finalization-stage telemetry configurator that wires infrastructure-level
/// processors (PII, rate limiting, cost/tool enrichment) and multi-backend
/// exporters into the OTel pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Runs at Order 300 (Finalization) — after all application and domain sources
/// have been registered (Order 100-299). This ensures processors see all spans
/// from all sources.
/// </para>
/// <para>
/// Processor pipeline order within this configurator:
/// <list type="number">
///   <item><description>PII filtering — scrub sensitive attributes first</description></item>
///   <item><description>Rate limiting — drop excess throughput</description></item>
///   <item><description>Cost and tool enrichment — record token cost and tool metrics</description></item>
/// </list>
/// PII filtering runs first so that even dropped spans never have sensitive data
/// in memory longer than necessary.
/// </para>
/// <para>
/// Trace sampling is not performed here. Tail-based sampling is a Collector-tier
/// concern (see the observability architecture guide): the SDK exports every span
/// and the OpenTelemetry Collector's <c>tail_sampling</c> processor decides what to
/// keep. A prior in-app tail sampler was removed because a <see cref="BaseProcessor{T}.OnEnd"/>
/// no-op cannot prevent the OTLP/Azure Monitor exporters from having already enqueued
/// every span.
/// </para>
/// </remarks>
public sealed class ObservabilityTelemetryConfigurator : ITelemetryConfigurator
{
    private readonly ILogger<ObservabilityTelemetryConfigurator> _logger;
    private readonly IOptionsMonitor<Domain.Common.Config.AppConfig> _appConfig;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IBudgetTrackingService _budgetTracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservabilityTelemetryConfigurator"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="appConfig">The application configuration.</param>
    /// <param name="loggerFactory">The logger factory for creating processor loggers.</param>
    /// <param name="budgetTracker">The budget tracking service for cost aggregation.</param>
    public ObservabilityTelemetryConfigurator(
        ILogger<ObservabilityTelemetryConfigurator> logger,
        IOptionsMonitor<Domain.Common.Config.AppConfig> appConfig,
        ILoggerFactory loggerFactory,
        IBudgetTrackingService budgetTracker)
    {
        _logger = logger;
        _appConfig = appConfig;
        _loggerFactory = loggerFactory;
        _budgetTracker = budgetTracker;
    }

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public void ConfigureTracing(TracerProviderBuilder builder)
    {
        var config = _appConfig.CurrentValue.Observability;

        // Database-client instrumentation: surface Npgsql's own spans (command + connection
        // activity) into the pipeline so DB time nests under the calling agent/HTTP span.
        // No-op unless a Postgres-backed store is in use (e.g. PostgresObservabilityStore);
        // Npgsql 10 emits OpenTelemetry-semconv span tags.
        builder.AddNpgsql();
        _logger.LogInformation("Npgsql tracing instrumentation registered");

        // Snapshot config for processors (they use IOptions, not IOptionsMonitor)
        var optionsSnapshot = Options.Create(_appConfig.CurrentValue);

        // Processor 1: PII filtering (scrub before anything else)
        if (config.PiiFiltering.Enabled)
        {
            builder.AddProcessor(new PiiFilteringProcessor(
                _loggerFactory.CreateLogger<PiiFilteringProcessor>(),
                optionsSnapshot));
            _logger.LogInformation("PII filtering processor registered");
        }

        // Processor 2: Rate limiting
        if (config.RateLimiting.Enabled)
        {
            builder.AddProcessor(new RateLimitingProcessor(
                _loggerFactory.CreateLogger<RateLimitingProcessor>(),
                optionsSnapshot));
            _logger.LogInformation("Rate limiting processor registered ({SpansPerSec} spans/sec)",
                config.RateLimiting.SpansPerSecond);
        }

        // Processor 3: LLM token tracking — BEFORE sampling so cost is recorded
        // even for spans that get sampled out
        builder.AddProcessor(new LlmTokenTrackingProcessor(
            _loggerFactory.CreateLogger<LlmTokenTrackingProcessor>(),
            optionsSnapshot,
            _budgetTracker));
        _logger.LogInformation("LLM token tracking processor registered");

        // Processor 4: Tool effectiveness — BEFORE sampling for same reason
        builder.AddProcessor(new ToolEffectivenessProcessor(
            _loggerFactory.CreateLogger<ToolEffectivenessProcessor>()));
        _logger.LogInformation("Tool effectiveness processor registered");

        // Processor 5: Tool usefulness scoring — composite 0-1 score per tool call
        builder.AddProcessor(new ToolUsefulnessProcessor(
            _loggerFactory.CreateLogger<ToolUsefulnessProcessor>()));
        _logger.LogInformation("Tool usefulness processor registered");

        // Processor 6: Causal attribution — bridges agent.tool.name → gen_ai.tool.name,
        // adds input hash and result category, reads eval context from baggage
        builder.AddProcessor(new CausalSpanAttributionProcessor(
            _loggerFactory.CreateLogger<CausalSpanAttributionProcessor>()));
        _logger.LogInformation("Causal span attribution processor registered");

        // Trace sampling is intentionally NOT done here. Tail-based sampling belongs at the
        // OpenTelemetry Collector tier (tail_sampling processor); see the observability
        // architecture guide. An in-app BaseProcessor cannot drop spans the exporters have
        // already enqueued, so the SDK exports every span and the collector decides.

        // OTLP exporter is registered in OpenTelemetryServiceCollectionExtensions (pre-build phase)

        // Exporters: Azure Monitor
        if (config.Exporters.AzureMonitor.Enabled
            && !string.IsNullOrWhiteSpace(config.Exporters.AzureMonitor.ConnectionString))
        {
            builder.AddAzureMonitorTraceExporter(options =>
            {
                options.ConnectionString = config.Exporters.AzureMonitor.ConnectionString;
            });
            _logger.LogInformation("Azure Monitor trace exporter registered");
        }
    }

    /// <inheritdoc />
    public void ConfigureMetrics(MeterProviderBuilder builder)
    {
        var config = _appConfig.CurrentValue.Observability;

        // Database-client metrics: connection-pool occupancy/leaks, command duration,
        // bytes read/written. Meter name "Npgsql"; Npgsql 10 renamed these counters to the
        // OpenTelemetry db.client.* semantic conventions. No-op without a Postgres store.
        builder.AddMeter("Npgsql");
        _logger.LogInformation("Npgsql metrics instrumentation registered");

        // OTLP exporter is registered in OpenTelemetryServiceCollectionExtensions (pre-build phase)

        // Exporters: Azure Monitor metrics
        if (config.Exporters.AzureMonitor.Enabled
            && !string.IsNullOrWhiteSpace(config.Exporters.AzureMonitor.ConnectionString))
        {
            builder.AddAzureMonitorMetricExporter(options =>
            {
                options.ConnectionString = config.Exporters.AzureMonitor.ConnectionString;
            });
            _logger.LogInformation("Azure Monitor metrics exporter registered");
        }

        // Prometheus: configured at ASP.NET Core level (app.UseOpenTelemetryPrometheusScrapingEndpoint)
        // so it's wired in Presentation. Config is read from AppConfig.Observability.Exporters.Prometheus.
    }

}
