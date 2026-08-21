using Application.AI.Common.Interfaces.Telemetry;
using Application.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using Domain.Common.Telemetry;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Reflection;

namespace Presentation.Common.Extensions;

/// <summary>
/// Extension methods for configuring OpenTelemetry tracing and metrics pipelines.
/// Supports both web (ASP.NET Core) and desktop (console/worker) application modes
/// with a shared resource builder and <see cref="ITelemetryConfigurator"/> extensibility.
/// </summary>
/// <remarks>
/// <para>
/// This class registers the core OTel pipeline: resource attributes, the harness's own
/// <see cref="AppInstrument"/> source/meter, ASP.NET Core and HTTP client instrumentation,
/// and Prometheus metrics export. Domain-specific sources (AI SDKs, MCP, etc.) are added
/// by <see cref="ITelemetryConfigurator"/> implementations discovered from DI.
/// </para>
/// <para>
/// <strong>Must be called after all project dependencies are registered</strong> so that
/// <c>ITelemetryConfigurator</c> instances (e.g., <c>AiTelemetryConfigurator</c>,
/// <c>ObservabilityTelemetryConfigurator</c>) are available for pipeline composition.
/// </para>
/// </remarks>
public static class OpenTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Configures the OpenTelemetry pipeline for the application. Enables Semantic Kernel,
    /// Azure SDK, and GenAI content recording via AppContext switches, then delegates to
    /// either <see cref="AddWebTelemetry"/> or <see cref="AddDesktopTelemetry"/> based
    /// on whether the entry assembly appears in <c>appConfig.Observability.WebTelemetryProjects</c>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="appConfig">
    /// Application configuration providing resource attributes and the web/desktop project list.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOpenTelemetry(
        this IServiceCollection services,
        AppConfig appConfig)
    {
        // Enable Semantic Kernel and Azure SDK telemetry
        AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics", true);
        AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

        // Sensitive telemetry (GenAI prompt/completion content) is opt-in via config.
        // When false, only non-sensitive metadata (model, token counts) is captured.
        AppContext.SetSwitch(
            "Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive",
            appConfig.Observability.EnableSensitiveTelemetry);

        // Register the shared resource builder as a singleton for consistent attributes
        var resourceBuilder = CreateResourceBuilder(appConfig);
        services.AddSingleton(resourceBuilder);

        var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? "UnknownService";
        var isWebProject = appConfig.Observability.WebTelemetryProjects
            .Contains(entryAssemblyName, StringComparer.OrdinalIgnoreCase);

        if (isWebProject)
            services.AddWebTelemetry(appConfig);
        else
            services.AddDesktopTelemetry();

        // Logs signal — bridges ILogger records into OTel so they reach the same
        // backend as traces/metrics (closing the gap where app logs never left the
        // local console/file sinks). Wired identically for both host shapes via the
        // hosting integration's ILogger bridge, and OFF by default.
        services.AddLogsSignal(appConfig);

        return services;
    }

    /// <summary>
    /// Configures OpenTelemetry for ASP.NET Core web applications with tracing and metrics
    /// pipelines that include HTTP, ASP.NET Core instrumentation, Prometheus export,
    /// and all registered <see cref="ITelemetryConfigurator"/> extensions.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="IDeferredTracerProviderBuilder"/> and <see cref="IDeferredMeterProviderBuilder"/>
    /// to defer configurator resolution until the real <see cref="IServiceProvider"/> is built,
    /// avoiding the <c>BuildServiceProvider()</c> anti-pattern that creates duplicate singletons.
    /// </remarks>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c> so
    /// <c>OpenTelemetryTracingExceptionRedactionTests</c> can call it directly to verify the redaction
    /// wiring against real DI-resolved options — <see cref="AddOpenTelemetry"/>'s own web/desktop
    /// branch keys off <see cref="System.Reflection.Assembly.GetEntryAssembly"/>, which is the test
    /// host in a test process, not a project named in <c>Observability:WebTelemetryProjects</c>, so
    /// that entry point never reaches this method from a test.
    /// </remarks>
    internal static IServiceCollection AddWebTelemetry(this IServiceCollection services, AppConfig appConfig)
    {
        // PostConfigure, not Configure: this template is meant to be cloned and extended, and a
        // consumer's own AddAspNetCoreInstrumentation(o => ...)/AddHttpClientInstrumentation(o => ...)
        // call elsewhere in their composition root would otherwise be free to run after this Configure
        // and silently flip RecordException back on or replace EnrichWithException, undoing the
        // redaction with no signal that it happened. PostConfigure always runs after every Configure
        // delegate for these options, regardless of registration order, closing that gap structurally
        // instead of relying on this method running last.
        services.AddOptions<AspNetCoreTraceInstrumentationOptions>()
            .PostConfigure<IContentRedactionFilter>((options, filter) =>
            {
                options.RecordException = false;
                options.EnrichWithException = BuildRedactingExceptionEnricher(filter);
            });

        services.AddOptions<HttpClientTraceInstrumentationOptions>()
            .PostConfigure<IContentRedactionFilter>((options, filter) =>
            {
                options.RecordException = false;
                options.EnrichWithException = BuildRedactingExceptionEnricher(filter);
            });

        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                // Base instrumentation + exporters configured pre-build
                ConfigureTracerProviderBuilder(builder, appConfig);

                // Defer configurator resolution to the real service provider
                ((IDeferredTracerProviderBuilder)builder).Configure((sp, deferredBuilder) =>
                {
                    var configurators = sp.GetServices<ITelemetryConfigurator>()
                        .OrderBy(c => c.Order);

                    foreach (var configurator in configurators)
                        configurator.ConfigureTracing(deferredBuilder);
                });
            })
            .WithMetrics(builder =>
            {
                // Base instrumentation + exporters configured pre-build
                ConfigureMeterProviderBuilder(builder, appConfig);

                // Defer configurator resolution to the real service provider
                ((IDeferredMeterProviderBuilder)builder).Configure((sp, deferredBuilder) =>
                {
                    var configurators = sp.GetServices<ITelemetryConfigurator>()
                        .OrderBy(c => c.Order);

                    foreach (var configurator in configurators)
                        configurator.ConfigureMetrics(deferredBuilder);
                });
            });

        return services;
    }

    /// <summary>
    /// Builds the exception-enrichment callback shared by ASP.NET Core and HttpClient trace
    /// instrumentation. Redacts the exception's message and full <see cref="Exception.ToString"/>
    /// text through <paramref name="filter"/> before either reaches the span — as the
    /// <c>exception.type</c> / <c>exception.message</c> tags this callback sets directly on the
    /// activity, and as the "exception" span event this callback constructs itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this callback creates the span event itself instead of scrubbing the one the
    /// instrumentation library creates.</strong> Confirmed against the .NET runtime source:
    /// <see cref="ActivityEvent"/> is an immutable <see langword="readonly struct"/> — its tags are
    /// copied into a private linked list at construction, with no public API to mutate an event
    /// already added to <see cref="Activity.Events"/>, and <see cref="Activity.Events"/> itself has
    /// no setter. A processor running after the span ends (the span-side sibling of
    /// <see cref="LogRecordRedactionProcessor"/>) cannot scrub that event's tags after the fact — so
    /// both instrumentation options this callback is registered on have <c>RecordException</c> set to
    /// <see langword="false"/> (see <see cref="AddWebTelemetry"/>), suppressing the library's own
    /// unredacted <c>Activity.AddException</c> call, and this callback calls it instead with content
    /// that is already safe.
    /// </para>
    /// <para>
    /// Passes an explicit <see cref="TagList"/> to <see cref="Activity.AddException"/> carrying both
    /// the short redacted message and the full redacted detail — confirmed against the runtime's
    /// <c>Activity.AddException</c> source: it only fills a tag from the exception's own (raw)
    /// <see cref="Exception.Message"/> / <see cref="Exception.ToString"/> when that tag is not already
    /// present in the list it's given, so pre-populating both here means none of the framework's own
    /// unredacted population ever runs. The full <see cref="Exception.ToString"/> text (not just
    /// <see cref="Exception.Message"/>) is what gets redacted for <c>exception.stacktrace</c> — it
    /// recursively includes every <see cref="Exception.InnerException"/>'s own message, so a secret
    /// nested in a wrapped exception's inner message is still caught even when the outer message is
    /// clean, matching <see cref="LogRecordRedactionProcessor"/>'s <c>RedactException</c> — identical
    /// reasoning on the log side.
    /// </para>
    /// </remarks>
    internal static Action<Activity, Exception> BuildRedactingExceptionEnricher(IContentRedactionFilter filter) =>
        (activity, exception) =>
        {
            var redactedMessage = filter.Redact(exception.Message, RedactionCategories.All);
            var redactedDetail = filter.Redact(exception.ToString(), RedactionCategories.All);

            activity.SetTag("exception.type", exception.GetType().FullName);
            activity.SetTag("exception.message", redactedMessage);

            var eventTags = new TagList
            {
                { "exception.message", redactedMessage },
                { "exception.stacktrace", redactedDetail }
            };
            activity.AddException(new RedactedSpanException(redactedMessage), in eventTags);
        };

    /// <summary>
    /// Configures OpenTelemetry for desktop/console applications by creating
    /// standalone <see cref="TracerProvider"/> and <see cref="MeterProvider"/>
    /// singletons with the same instrumentation as the web pipeline.
    /// </summary>
    private static IServiceCollection AddDesktopTelemetry(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var resourceBuilder = sp.GetRequiredService<ResourceBuilder>();
            var configurators = sp.GetServices<ITelemetryConfigurator>()
                .OrderBy(c => c.Order)
                .ToList();

            var builder = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddSource(AppInstrument.Source.Name)
                .SetSampler(new AlwaysOnSampler())
                .AddHttpClientInstrumentation();

            foreach (var configurator in configurators)
                configurator.ConfigureTracing(builder);

            return builder.Build()!;
        });

        services.AddSingleton(sp =>
        {
            var resourceBuilder = sp.GetRequiredService<ResourceBuilder>();
            var configurators = sp.GetServices<ITelemetryConfigurator>()
                .OrderBy(c => c.Order)
                .ToList();

            var builder = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(AppInstrument.Meter.Name)
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation();

            foreach (var configurator in configurators)
                configurator.ConfigureMetrics(builder);

            return builder.Build()!;
        });

        // The TracerProvider/MeterProvider singletons above are lazy: their factories run
        // only on first resolution, and nothing in a console/worker host resolves them, so
        // the OTel SDK would never be built and all telemetry would be silently dropped.
        // This hosted service forces both to materialize at host start.
        services.AddHostedService<Telemetry.DesktopTelemetryHostedService>();

        return services;
    }

    /// <summary>
    /// Wires the OpenTelemetry logs signal — the <c>ILogger</c> → OTel bridge — when
    /// <c>Observability:Logs:OtelExportEnabled</c> is set. No-op (and zero pipeline
    /// registration) when the flag is off, so hosts boot byte-identically by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <c>AddOpenTelemetry().WithLogging(...)</c> rather than a standalone
    /// <see cref="LoggerProvider"/>: the logs signal must originate from the host's
    /// <c>ILoggerFactory</c>, and <c>WithLogging</c> registers the bridging
    /// <c>ILoggerProvider</c> (aliased "OpenTelemetry") into DI where the standard
    /// factory picks it up — so it works uniformly for both the web host and the
    /// bare-<c>ServiceCollection</c> console hosts. This differs from the standalone
    /// tracer/meter providers, which are built manually because nothing resolves them
    /// on the console path; the logs bridge self-materializes on first log.
    /// </para>
    /// <para>
    /// <see cref="LogsConfig.MinExportLevel"/> is applied as a provider-scoped filter,
    /// so it caps what OTel <em>exports</em> without touching the local sinks' levels.
    /// </para>
    /// </remarks>
    private static IServiceCollection AddLogsSignal(this IServiceCollection services, AppConfig appConfig)
    {
        var logsConfig = appConfig.Observability.Logs;
        if (!logsConfig.OtelExportEnabled)
            return services;

        services.AddOpenTelemetry().WithLogging(
            configureBuilder: builder => ConfigureLoggerProviderBuilder(builder, appConfig),
            // Populate FormattedMessage so the rendered text (and any PII in it) is
            // present for the redactor to scrub and for exporters to ship.
            configureOptions: options => options.IncludeFormattedMessage = true);

        // Cap OTel export at MinExportLevel independent of the console/file/JSONL sinks,
        // which keep their own levels. Targets the bridge provider by its concrete type.
        if (Enum.TryParse<LogLevel>(logsConfig.MinExportLevel, ignoreCase: true, out var minLevel))
        {
            services.AddLogging(logging =>
                logging.AddFilter<OpenTelemetryLoggerProvider>(category: null, minLevel));
        }

        return services;
    }

    /// <summary>
    /// Configures the OTel logger pipeline: PII redaction first, then the OTLP logs
    /// exporter, then the shared resource attributes (resolved at build time so logs
    /// correlate with traces/metrics in the backend).
    /// </summary>
    private static void ConfigureLoggerProviderBuilder(LoggerProviderBuilder builder, AppConfig appConfig)
    {
        var logsConfig = appConfig.Observability.Logs;

        // Redaction is registered FIRST — ahead of the exporter — so PII is scrubbed
        // before the batch exporter snapshots the pooled record. The DI factory defers
        // construction to build time, resolving the shared content redactor.
        if (logsConfig.RedactionEnabled)
        {
            builder.AddProcessor(sp => new LogRecordRedactionProcessor(
                sp.GetRequiredService<IContentRedactionFilter>(),
                logsConfig,
                sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger<LogRecordRedactionProcessor>()));
        }

        // OTLP logs exporter — same endpoint/options as traces/metrics. Registered
        // pre-build (AddOtlpExporter calls ConfigureServices), and after redaction so
        // the scrub runs first in the OnEnd chain.
        var otlpConfig = appConfig.Observability.Exporters.Otlp;
        if (otlpConfig.Enabled)
        {
            builder.AddOtlpExporter("otlp-logs", options =>
                ConfigureOtlpOptions(options, otlpConfig));
        }

        // Resolve the ResourceBuilder singleton at provider-build time so logs carry the
        // same service.name/version resource attributes as the other signals.
        ((IDeferredLoggerProviderBuilder)builder).Configure((sp, b) =>
            b.SetResourceBuilder(sp.GetRequiredService<ResourceBuilder>()));
    }

    /// <summary>
    /// Configures the base tracer provider with the harness activity source,
    /// always-on sampling, and ASP.NET Core + HTTP client instrumentation.
    /// The <see cref="ResourceBuilder"/> is resolved from DI via
    /// <see cref="TracerProviderBuilderExtensions.ConfigureResource"/>.
    /// </summary>
    private static void ConfigureTracerProviderBuilder(TracerProviderBuilder builder, AppConfig appConfig)
    {
        builder
            .AddSource(AppInstrument.Source.Name)
            .SetSampler(new AlwaysOnSampler())
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        // OTLP exporter must be registered pre-build (AddOtlpExporter calls ConfigureServices)
        var otlpConfig = appConfig.Observability.Exporters.Otlp;
        if (otlpConfig.Enabled)
        {
            builder.AddOtlpExporter("otlp-traces", options =>
                ConfigureOtlpOptions(options, otlpConfig));
        }

        // Resolve the ResourceBuilder singleton at provider-build time
        ((IDeferredTracerProviderBuilder)builder).Configure((sp, b) =>
            b.SetResourceBuilder(sp.GetRequiredService<ResourceBuilder>()));
    }

    /// <summary>
    /// Configures the base meter provider with the harness meter, ASP.NET Core
    /// hosting/Kestrel meters, runtime instrumentation, and Prometheus export.
    /// The <see cref="ResourceBuilder"/> is resolved from DI via deferred configuration.
    /// </summary>
    private static void ConfigureMeterProviderBuilder(MeterProviderBuilder builder, AppConfig appConfig)
    {
        builder
            .AddMeter(AppInstrument.Meter.Name)
            .AddMeter("Microsoft.AspNetCore.Hosting")
            .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
            .SetExemplarFilter(ExemplarFilterType.TraceBased)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter();

        // OTLP exporter must be registered pre-build (AddOtlpExporter calls ConfigureServices)
        var otlpConfig = appConfig.Observability.Exporters.Otlp;
        if (otlpConfig.Enabled)
        {
            builder.AddOtlpExporter("otlp-metrics", options =>
                ConfigureOtlpOptions(options, otlpConfig));
        }

        // Resolve the ResourceBuilder singleton at provider-build time
        ((IDeferredMeterProviderBuilder)builder).Configure((sp, b) =>
            b.SetResourceBuilder(sp.GetRequiredService<ResourceBuilder>()));
    }

    private static void ConfigureOtlpOptions(OtlpExporterOptions options, OtlpExporterConfig config)
    {
        options.Endpoint = new Uri(config.Endpoint);
        options.Protocol = OtlpExportProtocol.Grpc;
        options.TimeoutMilliseconds = (int)config.Timeout.TotalMilliseconds;

        if (config.Headers.Count > 0)
        {
            options.Headers = string.Join(",",
                config.Headers.Select(h => $"{h.Key}={h.Value}"));
        }
    }

    /// <summary>
    /// Creates the shared <see cref="ResourceBuilder"/> with service identity attributes
    /// derived from the entry assembly and application configuration.
    /// </summary>
    /// <param name="appConfig">Application configuration for name and version.</param>
    /// <returns>A configured resource builder.</returns>
    private static ResourceBuilder CreateResourceBuilder(AppConfig appConfig)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var serviceName = entryAssembly?.GetName().Name ?? "UnknownService";
        var serviceVersion = appConfig.Common.ApplicationVersion;

        return ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion,
                serviceInstanceId: Environment.MachineName)
            .AddAttributes(new Dictionary<string, object>
            {
                ["app"] = appConfig.Common.ApplicationName,
                ["app.version"] = serviceVersion,
                ["app.namespace"] = entryAssembly?.GetName().Name ?? "Unknown"
            });
    }
}
