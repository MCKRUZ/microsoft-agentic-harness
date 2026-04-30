using Application.Common.Interfaces.Telemetry;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using Domain.Common.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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
    private static IServiceCollection AddWebTelemetry(this IServiceCollection services, AppConfig appConfig)
    {
        services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            options.RecordException = true;
            options.EnrichWithException = (activity, exception) =>
            {
                activity.SetTag("exception.type", exception.GetType().FullName);
                activity.SetTag("exception.message", exception.Message);
            };
        });

        services.Configure<HttpClientTraceInstrumentationOptions>(options =>
        {
            options.RecordException = true;
            options.EnrichWithException = (activity, exception) =>
            {
                activity.SetTag("exception.type", exception.GetType().FullName);
                activity.SetTag("exception.message", exception.Message);
            };
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

        return services;
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
            .AddView(instrument =>
            {
                if (instrument.Meter.Name == AppSourceNames.AgenticHarness)
                    return new MetricStreamConfiguration { Name = $"agentic_harness.{instrument.Name}" };
                return new MetricStreamConfiguration();
            })
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
