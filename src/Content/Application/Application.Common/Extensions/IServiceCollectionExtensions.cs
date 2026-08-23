using Application.Common.Logging;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

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

            if (!loggingConfig.SuppressConsoleOutput)
            {
                // Execution-aware console formatter
                builder.AddExecutionConsoleFormatter();

                // Fallback simple console for environments that don't support ANSI
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.SingleLine = true;
                    options.ColorBehavior = LoggerColorBehavior.Enabled;
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                });
            }

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

        // #457: redact every local sink the same way the OTel logging bridge already does — one
        // front door (ILoggerFactory.CreateLogger) rather than patching each current and future
        // ILoggerProvider individually. Replaces AddLogging's own ILoggerFactory registration with an
        // equivalent LoggerFactory (same providers, same filter/scope options, all resolved from this
        // same container) wrapped in RedactingLoggerFactory. ILocalLogRedactor is resolved lazily and
        // optionally: a host with no implementation registered (this project has no reference to
        // wherever IContentRedactionFilter lives, by design — see ILocalLogRedactor's remarks) gets an
        // unmodified pipeline, byte-identical to before this existed.
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp =>
        {
            var providers = sp.GetServices<ILoggerProvider>();
            var filterOptions = sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>();
            var factoryOptions = sp.GetRequiredService<IOptions<LoggerFactoryOptions>>();
            var scopeProvider = sp.GetService<IExternalScopeProvider>();

            ILoggerFactory inner = scopeProvider is not null
                ? new LoggerFactory(providers, filterOptions, factoryOptions, scopeProvider)
                : new LoggerFactory(providers, filterOptions, factoryOptions);

            var redactor = sp.GetService<ILocalLogRedactor>();
            return redactor is null ? inner : new RedactingLoggerFactory(inner, redactor);
        }));

        // Azure SDK EventSource-to-ILogger bridge (issue #383) — surfaces which credential
        // DefaultAzureCredential selected at Information level, without full EventSource tracing.
        services.AddAzureIdentityDiagnostics();

        return services;
    }
}
