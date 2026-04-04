using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Application.Common.Interfaces.Telemetry;

/// <summary>
/// Extensibility point for registering domain-specific telemetry sources,
/// processors, and meters into the OpenTelemetry pipeline. Implementations
/// are discovered via DI and applied in <see cref="Order"/> sequence during
/// pipeline setup.
/// </summary>
/// <remarks>
/// <para>
/// Order ranges: 0-99 Core, 100-199 Standard (harness), 200-299 Domain, 300+ Finalization.
/// </para>
/// <para>
/// This interface lives in Application.Common so that any layer can provide a
/// configurator without referencing the OTel registration infrastructure directly.
/// The actual registration (calling <c>ConfigureTracing</c> / <c>ConfigureMetrics</c>
/// on all discovered configurators) happens in the Presentation or Infrastructure layer.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class MyDomainTelemetryConfigurator : ITelemetryConfigurator
/// {
///     public int Order => 200;
///     public void ConfigureTracing(TracerProviderBuilder builder) =>
///         builder.AddSource("MyDomain.*");
///     public void ConfigureMetrics(MeterProviderBuilder builder) =>
///         builder.AddMeter("MyDomain.*");
/// }
/// </code>
/// </example>
public interface ITelemetryConfigurator
{
    /// <summary>
    /// Gets the execution order for this configurator.
    /// Lower values run first. Default convention: 100.
    /// </summary>
    int Order => 100;

    /// <summary>Registers trace sources, processors, and instrumentation.</summary>
    /// <param name="builder">The tracer provider builder.</param>
    void ConfigureTracing(TracerProviderBuilder builder);

    /// <summary>Registers meters and metric views.</summary>
    /// <param name="builder">The meter provider builder.</param>
    void ConfigureMetrics(MeterProviderBuilder builder);
}
