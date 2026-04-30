using Application.AI.Common.Interfaces;
using Application.Common.Interfaces.Telemetry;
using Domain.Common.Config;
using Infrastructure.Observability.Exporters;
using Infrastructure.Observability.Persistence;
using Infrastructure.Observability.Processors;
using Infrastructure.Observability.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        // Budget tracking service — cost spend state machine with ObservableGauge callbacks
        services.AddSingleton<IBudgetTrackingService>(sp =>
        {
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>();
            if (!config.CurrentValue.Observability.BudgetTracking.Enabled)
                return new NullBudgetTrackingService();
            return new BudgetTrackingService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BudgetTrackingService>>(),
                config);
        });

        // Agent config info service — ObservableGauge reporting agent configs as metric labels
        services.AddSingleton<AgentConfigInfoService>();
        services.AddSingleton<IAgentConfigReporter>(sp => sp.GetRequiredService<AgentConfigInfoService>());

        // Session health service — ObservableGauge reporting per-agent health score
        services.AddSingleton<ISessionHealthTracker, SessionHealthService>();

        // Observability store — PostgreSQL persistence for session/message/tool/audit data.
        // Falls back to NullObservabilityStore (no-op) when the connection string is missing,
        // but logs a warning so operators know session data is being silently dropped.
        services.AddSingleton<IObservabilityStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PostgresObservabilityStore>>();
            var config = sp.GetRequiredService<IOptionsMonitor<AppConfig>>();
            var connStr = config.CurrentValue.Observability.PostgresConnectionString;
            if (string.IsNullOrWhiteSpace(connStr))
            {
                logger.LogWarning(
                    "AppConfig:Observability:PostgresConnectionString is not configured. " +
                    "Session, message, and tool execution data will NOT be persisted. " +
                    "Run 'scripts/start-infrastructure.ps1' to start PostgreSQL.");
                return new NullObservabilityStore();
            }
            return new PostgresObservabilityStore(connStr, logger);
        });

        return services;
    }
}
