using Application.Common.Interfaces.Telemetry;
using Azure.Monitor.OpenTelemetry.Exporter;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Infrastructure.Observability.Exporters;

/// <summary>
/// Finalization-stage telemetry configurator that wires infrastructure-level
/// processors (sampling, PII, rate limiting) and multi-backend exporters
/// into the OTel pipeline.
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
///   <item><description>Tail-based sampling — buffer and evaluate complete traces</description></item>
/// </list>
/// PII filtering runs first so that even dropped/sampled-out spans never have
/// sensitive data in memory longer than necessary.
/// </para>
/// </remarks>
public sealed class ObservabilityTelemetryConfigurator : ITelemetryConfigurator
{
    private readonly ILogger<ObservabilityTelemetryConfigurator> _logger;
    private readonly IOptionsMonitor<Domain.Common.Config.AppConfig> _appConfig;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservabilityTelemetryConfigurator"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="appConfig">The application configuration.</param>
    /// <param name="loggerFactory">The logger factory for creating processor loggers.</param>
    public ObservabilityTelemetryConfigurator(
        ILogger<ObservabilityTelemetryConfigurator> logger,
        IOptionsMonitor<Domain.Common.Config.AppConfig> appConfig,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _appConfig = appConfig;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public void ConfigureTracing(TracerProviderBuilder builder)
    {
        var config = _appConfig.CurrentValue.Observability;

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
            optionsSnapshot));
        _logger.LogInformation("LLM token tracking processor registered");

        // Processor 4: Tool effectiveness — BEFORE sampling for same reason
        builder.AddProcessor(new ToolEffectivenessProcessor(
            _loggerFactory.CreateLogger<ToolEffectivenessProcessor>()));
        _logger.LogInformation("Tool effectiveness processor registered");

        // Processor 5: Causal attribution — bridges agent.tool.name → gen_ai.tool.name,
        // adds input hash and result category, reads eval context from baggage
        builder.AddProcessor(new CausalSpanAttributionProcessor(
            _loggerFactory.CreateLogger<CausalSpanAttributionProcessor>()));
        _logger.LogInformation("Causal span attribution processor registered");

        // Processor 6: Tail-based sampling (last — all metrics already recorded)
        if (config.Sampling.Enabled)
        {
            builder.AddProcessor(new TailBasedSamplingProcessor(
                _loggerFactory.CreateLogger<TailBasedSamplingProcessor>(),
                optionsSnapshot));
            _logger.LogInformation("Tail-based sampling processor registered ({SampleRate}%)",
                config.Sampling.DefaultSamplingPercentage);
        }

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
