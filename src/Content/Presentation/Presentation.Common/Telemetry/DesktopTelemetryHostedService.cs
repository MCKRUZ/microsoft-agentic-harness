using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Presentation.Common.Telemetry;

/// <summary>
/// Eagerly builds the desktop (console/worker) OpenTelemetry providers at host start.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TracerProvider"/> and <see cref="MeterProvider"/> are registered in the
/// desktop path as lazy singleton factories. A .NET DI singleton factory executes only on
/// first resolution, and no production code in a console/worker host ever resolves these
/// providers. Without resolution the OTel SDK is never built, no listener attaches to the
/// harness's <c>AppInstrument</c> source/meter, and every trace and metric from the agent
/// execution pipeline is silently dropped — even though observability is a headline feature
/// of the template.
/// </para>
/// <para>
/// This hosted service forces both providers to materialize when the host starts, so the
/// SDK pipeline (sampler, instrumentation, OTLP exporter, and all
/// <c>ITelemetryConfigurator</c> implementations) is wired before the first activity is
/// emitted. The web path does not need this — <c>AddOpenTelemetry()</c> registers its own
/// hosted service that builds the providers eagerly.
/// </para>
/// <para>
/// The providers are owned by the DI container (registered as singletons) and are disposed
/// by the container on shutdown, which flushes any buffered telemetry. This service only
/// triggers construction; it does not dispose them.
/// </para>
/// </remarks>
public sealed class DesktopTelemetryHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DesktopTelemetryHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopTelemetryHostedService"/> class.
    /// </summary>
    /// <param name="serviceProvider">The root service provider used to resolve the OTel providers.</param>
    /// <param name="logger">Logger for startup diagnostics.</param>
    public DesktopTelemetryHostedService(
        IServiceProvider serviceProvider,
        ILogger<DesktopTelemetryHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the <see cref="TracerProvider"/> and <see cref="MeterProvider"/> singletons,
    /// forcing their lazy factories to build the OTel SDK pipeline and attach listeners.
    /// </summary>
    /// <param name="cancellationToken">Token to observe while starting.</param>
    /// <returns>A completed task — provider construction is synchronous.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolution is the whole point: it executes the singleton factories that call
        // Sdk.CreateTracerProviderBuilder()...Build() / Sdk.CreateMeterProviderBuilder()...Build().
        _ = _serviceProvider.GetRequiredService<TracerProvider>();
        _ = _serviceProvider.GetRequiredService<MeterProvider>();

        _logger.LogInformation(
            "Desktop OpenTelemetry providers initialized — tracing and metrics pipelines are live.");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
