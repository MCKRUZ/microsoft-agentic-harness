using Application.AI.Common.Interfaces.Telemetry;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Proves the OpenTelemetry logs signal is wired only when
/// <c>Observability:Logs:OtelExportEnabled</c> is set. When off, no logs pipeline
/// is registered at all, so hosts boot byte-identically to before the feature
/// existed; when on, the OTel <see cref="LoggerProvider"/> (the ILogger→OTel bridge)
/// is present in the container.
/// </summary>
/// <remarks>
/// Asserts on the registered <see cref="ServiceDescriptor"/> set rather than building
/// the provider: materializing the <see cref="LoggerProvider"/> would resolve the
/// deferred resource builder and content redactor, which belong to the full
/// composition root, not this focused wiring check.
/// </remarks>
public class OpenTelemetryLogsSignalTests
{
    private static ServiceCollection ServicesWithLogsExport(bool enabled)
    {
        var appConfig = new AppConfig();
        appConfig.Observability.Logs.OtelExportEnabled = enabled;

        var services = new ServiceCollection();
        services.AddOpenTelemetry(appConfig);
        return services;
    }

    [Fact]
    public void AddOpenTelemetry_LogsExportEnabled_RegistersOtelLoggerProvider()
    {
        var services = ServicesWithLogsExport(enabled: true);

        services.Should().Contain(
            d => d.ServiceType == typeof(LoggerProvider),
            "enabling Observability:Logs:OtelExportEnabled must wire the ILogger→OTel bridge");
    }

    [Fact]
    public void AddOpenTelemetry_LogsExportDisabled_DoesNotRegisterLoggerProvider()
    {
        var services = ServicesWithLogsExport(enabled: false);

        services.Should().NotContain(
            d => d.ServiceType == typeof(LoggerProvider),
            "with the flag off there must be no logs pipeline, so the host is unchanged");
    }

    /// <summary>A synchronous "exporter" that snapshots each record's rendered message.</summary>
    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        public List<string?> Messages { get; } = [];

        public override void OnEnd(LogRecord data) => Messages.Add(data.FormattedMessage);
    }

    [Fact]
    public void ProductionWiring_LogsExportEnabled_ScrubsPiiEndToEnd()
    {
        // Exercises the REAL composition — AddOpenTelemetry(appConfig) → AddLogsSignal →
        // ConfigureLoggerProviderBuilder — with the shipped DefaultContentRedactionFilter,
        // then appends a capturing processor (registered after the production redactor, so
        // it observes the scrubbed record) to prove PII is redacted before export. OTLP is
        // left disabled so no real exporter/endpoint is needed. This closes the story's §8
        // "WithLogging pipeline emits redacted LogRecords end-to-end" requirement against the
        // production wiring rather than a hand-built LoggerFactory. Logs wiring is identical
        // for web and desktop hosts (AddLogsSignal runs outside the host-shape branch), so a
        // single composition covers both shapes.
        var appConfig = new AppConfig();
        appConfig.Observability.Logs.OtelExportEnabled = true;   // redaction on by default
        appConfig.Observability.Exporters.Otlp.Enabled = false;  // no real export endpoint

        var capture = new CapturingProcessor();
        var services = new ServiceCollection();
        services.AddSingleton<IContentRedactionFilter, DefaultContentRedactionFilter>();
        services.AddOpenTelemetry(appConfig);
        // Append the capturer to the same logger pipeline, after the production stages.
        services.AddOpenTelemetry().WithLogging(builder => builder.AddProcessor(capture));

        using var provider = services.BuildServiceProvider();
        // Force the logger pipeline to build (executes ConfigureLoggerProviderBuilder).
        _ = provider.GetRequiredService<LoggerProvider>();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("test.e2e");
        logger.LogInformation("user {Email} signed in", "alice@example.com");

        capture.Messages.Should().ContainSingle();
        capture.Messages[0].Should().NotContain("alice@example.com");
        capture.Messages[0].Should().Contain("[REDACTED:Email]");
    }
}
