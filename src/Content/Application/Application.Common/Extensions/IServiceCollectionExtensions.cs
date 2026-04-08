using Application.Common.Logging;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Application.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> to configure
/// agentic harness application services.
/// </summary>
/// <remarks>
/// OpenTelemetry exporter registration (Azure Monitor, OTLP, Prometheus) lives in
/// the Infrastructure layer where concrete exporter packages are referenced.
/// This class configures only Application-layer concerns.
/// </remarks>
public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Configures the logging pipeline with all agentic harness providers.
    /// Provider activation is driven by <see cref="LoggingConfig"/> settings.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="loggingConfig">Logging configuration for provider activation decisions.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>Providers registered:</para>
    /// <list type="bullet">
    ///   <item><description><strong>ExecutionConsoleFormatter</strong> — always enabled, identity-aware console output</description></item>
    ///   <item><description><strong>SimpleConsole</strong> — fallback with timestamps and scopes</description></item>
    ///   <item><description><strong>NamedPipe</strong> — when <c>PipeName</c> is configured</description></item>
    ///   <item><description><strong>FileLogger</strong> — when <c>LogsBasePath</c> is configured</description></item>
    ///   <item><description><strong>StructuredJsonLogger</strong> — when <c>LogsBasePath</c> + <c>EnableStructuredJson</c></description></item>
    ///   <item><description><strong>InMemoryRingBuffer</strong> — always enabled for diagnostics</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection ConfigureLogging(
        this IServiceCollection services,
        LoggingConfig loggingConfig)
    {
        services.AddLogging(builder =>
        {
            builder.ClearProviders();

            // Execution-aware console formatter (always enabled)
            builder.AddExecutionConsoleFormatter();

            // Fallback simple console for environments that don't support ANSI
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.ColorBehavior = LoggerColorBehavior.Enabled;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            });

            // Named pipe for real-time streaming to a separate viewer
            if (!string.IsNullOrWhiteSpace(loggingConfig.PipeName))
                builder.AddNamedPipe();

            // File-based logging (human-readable + optional structured JSON)
            if (!string.IsNullOrWhiteSpace(loggingConfig.LogsBasePath))
            {
                builder.AddFileLogger();

                if (loggingConfig.EnableStructuredJson)
                    builder.AddStructuredJsonLogger();
            }

            // In-memory ring buffer for diagnostics endpoints (always enabled)
            builder.AddInMemoryRingBuffer();
        });

        return services;
    }
}
