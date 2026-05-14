using Domain.Common.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Presentation.AgentHub.Tests.Telemetry.Fixtures;

/// <summary>
/// E2E test factory that extends <see cref="MetricsIntegrationFactory"/> by replacing
/// the in-process Prometheus exporter with an OTLP exporter targeting a Testcontainers
/// OTel Collector, and pointing <see cref="Presentation.AgentHub.Services.PrometheusQueryService"/>
/// at the Testcontainers Prometheus instance so <c>/api/metrics/*</c> endpoints return real data.
/// </summary>
/// <remarks>
/// <para>
/// Keeps all mock AI services from the parent factory (stub agent, no-op safety, etc.)
/// so no real LLM calls are made. The metrics pipeline is fully real:
/// App → OTLP → Collector (filter/namespace) → Prometheus → PrometheusQueryService → MetricsController.
/// </para>
/// <para>
/// The <c>service.name</c> resource attribute is set to <c>Presentation.AgentHub</c>
/// to match the collector's <c>filter/app_only</c> processor regex.
/// </para>
/// </remarks>
public class MetricsE2EFactory : MetricsIntegrationFactory
{
    private readonly string _otlpEndpoint;
    private readonly string _prometheusEndpoint;

    /// <summary>
    /// Creates a new E2E factory that exports OTLP metrics to the given collector endpoint
    /// and proxies Prometheus queries to the given Prometheus endpoint.
    /// </summary>
    /// <param name="otlpGrpcEndpoint">
    /// The collector's OTLP gRPC endpoint (e.g., <c>http://localhost:4317</c>).
    /// </param>
    /// <param name="prometheusEndpoint">
    /// The Prometheus HTTP API endpoint (e.g., <c>http://localhost:9090</c>).
    /// Configures <c>PrometheusQueryService</c> so <c>/api/metrics/*</c> controller
    /// endpoints query the real Testcontainers Prometheus instance.
    /// </param>
    public MetricsE2EFactory(string otlpGrpcEndpoint, string prometheusEndpoint)
    {
        _otlpEndpoint = otlpGrpcEndpoint;
        _prometheusEndpoint = prometheusEndpoint;
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Point PrometheusQueryService at the Testcontainers Prometheus instance
        // so /api/metrics/* endpoints return real data from the E2E pipeline.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:Prometheus:BaseUrl"] = _prometheusEndpoint,
                ["AppConfig:Prometheus:EnableDemoData"] = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove any MeterProvider registrations from the parent factory
            // and reconfigure with OTLP export to the Testcontainers collector.
            services.RemoveAll<MeterProvider>();
            services.AddOpenTelemetry()
                .ConfigureResource(r => r
                    .AddService(
                        serviceName: "Presentation.AgentHub",
                        serviceVersion: "1.0.0-test",
                        serviceInstanceId: Environment.MachineName))
                .WithMetrics(m =>
                {
                    m.AddMeter(AppSourceNames.AgenticHarness);
                    m.AddOtlpExporter("otlp-e2e", options =>
                    {
                        options.Endpoint = new Uri(_otlpEndpoint);
                        options.Protocol = OtlpExportProtocol.Grpc;
                        options.TimeoutMilliseconds = 10_000;
                    });
                });
        });
    }
}
