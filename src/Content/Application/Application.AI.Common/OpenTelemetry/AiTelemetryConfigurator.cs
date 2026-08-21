using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.OpenTelemetry.Instruments;
using Application.AI.Common.OpenTelemetry.Processors;
using Application.Common.Interfaces.Telemetry;
using Domain.Common.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Application.AI.Common.OpenTelemetry;

/// <summary>
/// Registers AI-specific telemetry sources, meters, and processors into the
/// OTel pipeline. Layers on top of <see cref="Application.Common.OpenTelemetry.AppTelemetryConfigurator"/>
/// which handles the base harness sources.
/// </summary>
/// <remarks>
/// Order 150: runs after the base app configurator (100) but before
/// domain-specific configurators (200+) and finalization (300+).
/// </remarks>
public sealed class AiTelemetryConfigurator : ITelemetryConfigurator
{
    private readonly IContentRedactionFilter _redactionFilter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiTelemetryConfigurator"/> class.
    /// </summary>
    /// <param name="redactionFilter">Passed to <see cref="AgentFrameworkSpanProcessor"/> so
    /// tool-result content is redacted before it reaches <c>gen_ai.event.content</c>.</param>
    public AiTelemetryConfigurator(IContentRedactionFilter redactionFilter)
    {
        ArgumentNullException.ThrowIfNull(redactionFilter);
        _redactionFilter = redactionFilter;
    }

    /// <inheritdoc />
    public int Order => 150;

    /// <inheritdoc />
    public void ConfigureTracing(TracerProviderBuilder builder)
    {
        builder
            .AddSource(AiSourceNames.MicrosoftAgentsAI)
            .AddSource(AiSourceNames.MicrosoftExtensionsAI)
            .AddSource(AiSourceNames.SemanticKernel)
            .AddProcessor(new AgentFrameworkSpanProcessor(_redactionFilter))
            .AddProcessor(new ConversationSpanProcessor());
    }

    /// <inheritdoc />
    public void ConfigureMetrics(MeterProviderBuilder builder)
    {
        builder
            .AddMeter(AiSourceNames.MicrosoftAgentsAI)
            .AddMeter(AiSourceNames.MicrosoftExtensionsAI)
            .AddMeter(AiSourceNames.SemanticKernel);
    }
}
