using Application.Common.Interfaces.Telemetry;
using Domain.Common.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Application.Common.OpenTelemetry;

/// <summary>
/// Registers application-level telemetry sources and meters into the OTel pipeline.
/// Provides the base harness telemetry that all layers can emit against.
/// </summary>
/// <remarks>
/// Order 100: runs as the standard application configurator. AI-specific configurators
/// (150) layer on top with SDK subscriptions and AI-specific processors.
/// </remarks>
public sealed class AppTelemetryConfigurator : ITelemetryConfigurator
{
    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public void ConfigureTracing(TracerProviderBuilder builder)
    {
        builder
            .AddSource(AppSourceNames.AgenticHarness)
            .AddSource(AppSourceNames.AgenticHarnessMediatR);
    }

    /// <inheritdoc />
    public void ConfigureMetrics(MeterProviderBuilder builder)
    {
        builder
            .AddMeter(AppSourceNames.AgenticHarness);
    }
}
