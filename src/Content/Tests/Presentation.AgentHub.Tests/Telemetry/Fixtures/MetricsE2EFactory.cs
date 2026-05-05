using Domain.Common.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Presentation.AgentHub.Tests.Telemetry.Fixtures;

/// <summary>
/// E2E test factory that extends <see cref="MetricsIntegrationFactory"/> by replacing
/// the in-process Prometheus exporter with an OTLP exporter targeting a Testcontainers
/// OTel Collector. The collector forwards metrics to an external Prometheus instance.
/// </summary>
/// <remarks>
/// <para>
/// Keeps all mock AI services from the parent factory (stub agent, no-op safety, etc.)
/// so no real LLM calls are made. The only difference is the metrics export pipeline:
/// metrics flow through the real OTel Collector config (filter/app_only, namespace prefixing)
/// into Prometheus, enabling true end-to-end validation of the observability pipeline.
/// </para>
/// <para>
/// The <c>service.name</c> resource attribute is set to <c>Presentation.AgentHub</c>
/// to match the collector's <c>filter/app_only</c> processor regex.
/// </para>
/// </remarks>
public class MetricsE2EFactory : MetricsIntegrationFactory
{
    private readonly string _otlpEndpoint;

    /// <summary>
    /// Creates a new E2E factory that exports OTLP metrics to the given collector endpoint.
    /// </summary>
    /// <param name="otlpGrpcEndpoint">
    /// The collector's OTLP gRPC endpoint (e.g., <c>http://localhost:4317</c>).
    /// </param>
    public MetricsE2EFactory(string otlpGrpcEndpoint)
    {
        _otlpEndpoint = otlpGrpcEndpoint;
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

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
