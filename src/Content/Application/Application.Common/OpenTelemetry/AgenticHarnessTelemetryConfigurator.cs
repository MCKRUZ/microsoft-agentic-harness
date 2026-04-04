using Application.Common.Interfaces.Telemetry;
using Application.Common.OpenTelemetry.Instruments;
using Application.Common.OpenTelemetry.Processors;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Application.Common.OpenTelemetry;

/// <summary>
/// Registers all harness-level telemetry sources, meters, and processors into the
/// OTel pipeline. Follows the <see cref="ITelemetryConfigurator"/> extensibility pattern
/// so it is automatically discovered and applied during pipeline setup.
/// </summary>
/// <remarks>
/// Order 150: runs after core framework configurators (0-99) but before
/// domain-specific configurators (200+) and finalization (300+).
/// </remarks>
public sealed class AgenticHarnessTelemetryConfigurator : ITelemetryConfigurator
{
    /// <inheritdoc />
    public int Order => 150;

    /// <inheritdoc />
    public void ConfigureTracing(TracerProviderBuilder builder)
    {
        builder
            .AddSource(TelemetrySourceNames.AgenticHarness)
            .AddSource(TelemetrySourceNames.AgenticHarnessMediatR)
            .AddSource(TelemetrySourceNames.MicrosoftAgentsAI)
            .AddSource(TelemetrySourceNames.MicrosoftExtensionsAI)
            .AddSource(TelemetrySourceNames.SemanticKernel)
            .AddProcessor(new AgentFrameworkSpanProcessor())
            .AddProcessor(new ConversationSpanProcessor());
    }

    /// <inheritdoc />
    public void ConfigureMetrics(MeterProviderBuilder builder)
    {
        builder
            .AddMeter(TelemetrySourceNames.AgenticHarness)
            .AddMeter(TelemetrySourceNames.MicrosoftAgentsAI)
            .AddMeter(TelemetrySourceNames.MicrosoftExtensionsAI)
            .AddMeter(TelemetrySourceNames.SemanticKernel);
    }
}
