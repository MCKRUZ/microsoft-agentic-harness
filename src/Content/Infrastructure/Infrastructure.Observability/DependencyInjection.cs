using Application.Common.Interfaces.Telemetry;
using Infrastructure.Observability.Exporters;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Observability;

/// <summary>
/// Dependency injection configuration for the Infrastructure.Observability layer.
/// Registers the observability pipeline telemetry configurator that adds tail-based
/// sampling, PII filtering, rate limiting, and multi-backend export.
/// </summary>
/// <remarks>
/// <para>
/// Called from the Presentation composition root:
/// <code>
/// services.AddInfrastructureObservabilityDependencies();
/// </code>
/// </para>
/// <para>
/// The <see cref="ObservabilityTelemetryConfigurator"/> registers at Order 300
/// (Finalization), ensuring all domain sources and processors are already wired
/// before the infrastructure processors filter, sample, and export.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all Infrastructure.Observability dependencies into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddInfrastructureObservabilityDependencies(
        this IServiceCollection services)
    {
        // Observability pipeline configurator — adds processors and exporters at Order 300
        services.AddSingleton<ITelemetryConfigurator, ObservabilityTelemetryConfigurator>();

        return services;
    }
}
