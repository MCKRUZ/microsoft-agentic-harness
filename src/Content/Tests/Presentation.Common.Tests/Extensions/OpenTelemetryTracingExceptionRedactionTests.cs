using System.Diagnostics;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Proves the span-side exception redaction (#450) closes both leak paths a secret-bearing
/// exception can reach a trace through: the <c>exception.type</c>/<c>exception.message</c> tags
/// <c>BuildRedactingExceptionEnricher</c> sets directly on the activity, and the "exception" span
/// event it constructs itself (with <c>RecordException</c> switched off, so the instrumentation
/// library's own unredacted <c>Activity.AddException</c> call never runs — see
/// <see cref="OpenTelemetryServiceCollectionExtensions.AddOpenTelemetry"/>'s remarks).
/// </summary>
/// <remarks>
/// Exercises <c>BuildRedactingExceptionEnricher</c> directly rather than through
/// <c>AddOpenTelemetry(appConfig)</c>: the web/desktop host-shape branch that method itself picks
/// keys off <see cref="System.Reflection.Assembly.GetEntryAssembly"/>, which in a test process is
/// the test host, not a project named in <c>Observability:WebTelemetryProjects</c> — so the
/// full-composition entry point never reaches the web-only wiring this callback lives on. Testing
/// the callback itself is also the more precise unit: it pins exactly the redaction contract,
/// independent of instrumentation-library wiring already covered by
/// <c>OpenTelemetryLogsSignalTests</c>'s production-composition pattern on the log signal.
/// </remarks>
public sealed class OpenTelemetryTracingExceptionRedactionTests
{
    private static readonly IContentRedactionFilter Filter = new DefaultContentRedactionFilter();
    private static readonly ActivitySource Source = new(nameof(OpenTelemetryTracingExceptionRedactionTests));

    /// <summary>Starts a recorded activity — a listener must be registered or StartActivity returns null.</summary>
    private static Activity StartActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return Source.StartActivity("test-activity")!;
    }

    [Fact]
    public void Enricher_ExceptionMessageCarriesASecret_ActivityTagIsRedacted()
    {
        using var activity = StartActivity();
        var enrich = OpenTelemetryServiceCollectionExtensions.BuildRedactingExceptionEnricher(Filter);

        enrich(activity, new InvalidOperationException("contact admin@example.com for the connection string"));

        activity.GetTagItem("exception.message").Should().Be(
            "contact [REDACTED:Email] for the connection string");
        activity.GetTagItem("exception.type").Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public void Enricher_ExceptionMessageCarriesASecret_SpanEventIsRedactedNotRawException()
    {
        using var activity = StartActivity();
        var enrich = OpenTelemetryServiceCollectionExtensions.BuildRedactingExceptionEnricher(Filter);

        enrich(activity, new InvalidOperationException("contact admin@example.com for the connection string"));

        var exceptionEvent = activity.Events.Should().ContainSingle(e => e.Name == "exception").Subject;
        var tags = exceptionEvent.Tags.ToDictionary(t => t.Key, t => t.Value);

        tags["exception.message"].Should().Be("contact [REDACTED:Email] for the connection string");
        tags["exception.stacktrace"].As<string>().Should().NotContain("admin@example.com");
        tags["exception.stacktrace"].As<string>().Should().Contain("[REDACTED:Email]");
        // Self-announces redaction happened via the type name, rather than reporting the real
        // exception type as if RecordException's own (unredacted) auto-population had run.
        tags["exception.type"].Should().Be(typeof(RedactedSpanException).FullName);
    }

    [Fact]
    public void Enricher_ExceptionMessageCarriesNoSecret_TextIsUnchanged()
    {
        using var activity = StartActivity();
        var enrich = OpenTelemetryServiceCollectionExtensions.BuildRedactingExceptionEnricher(Filter);

        enrich(activity, new InvalidOperationException("the operation timed out"));

        activity.GetTagItem("exception.message").Should().Be("the operation timed out");
    }

    /// <summary>
    /// A wrapped exception's inner message can carry a secret the outer message never shows —
    /// <see cref="Exception.ToString"/> (not just <see cref="Exception.Message"/>) is what gets
    /// redacted for the event's <c>exception.stacktrace</c> tag specifically so this is still caught,
    /// matching <c>LogRecordRedactionProcessor.RedactException</c>'s identical reasoning on the log
    /// side.
    /// </summary>
    [Fact]
    public void Enricher_SecretOnlyInInnerException_StillRedactedInSpanEventDetail()
    {
        using var activity = StartActivity();
        var enrich = OpenTelemetryServiceCollectionExtensions.BuildRedactingExceptionEnricher(Filter);
        var inner = new InvalidOperationException("dispatch failed: admin@example.com");
        var outer = new InvalidOperationException("generic wrapper failure", inner);

        enrich(activity, outer);

        var exceptionEvent = activity.Events.Should().ContainSingle(e => e.Name == "exception").Subject;
        var stacktrace = exceptionEvent.Tags.ToDictionary(t => t.Key, t => t.Value)["exception.stacktrace"].As<string>();

        stacktrace.Should().NotContain("admin@example.com");
        stacktrace.Should().Contain("[REDACTED:Email]");
    }

    /// <summary>
    /// Pins the production DI wiring itself, not just the enricher function in isolation — a future
    /// OpenTelemetry package bump could change how <c>AddAspNetCoreInstrumentation</c>/
    /// <c>AddHttpClientInstrumentation</c> read their options and silently stop calling
    /// <c>EnrichWithException</c>, or a regression in <see cref="OpenTelemetryServiceCollectionExtensions.AddWebTelemetry"/>
    /// itself could drop the <c>PostConfigure</c> call. Resolving the real, DI-composed
    /// <see cref="IOptions{TOptions}"/> catches either without needing to build the full
    /// <c>TracerProvider</c> (which needs a live OTLP endpoint or exporters disabled).
    /// </summary>
    [Fact]
    public void AddWebTelemetry_ProductionWiring_RecordExceptionOffAndEnricherSet()
    {
        var appConfig = new AppConfig();
        appConfig.Observability.Exporters.Otlp.Enabled = false;

        var services = new ServiceCollection();
        services.AddSingleton<IContentRedactionFilter>(Filter);
        services.AddWebTelemetry(appConfig);

        using var provider = services.BuildServiceProvider();

        var aspNetCoreOptions = provider.GetRequiredService<IOptions<AspNetCoreTraceInstrumentationOptions>>().Value;
        aspNetCoreOptions.RecordException.Should().BeFalse(
            "the enricher builds the redacted exception event itself; the library's own auto-population must stay off");
        aspNetCoreOptions.EnrichWithException.Should().NotBeNull();

        var httpClientOptions = provider.GetRequiredService<IOptions<HttpClientTraceInstrumentationOptions>>().Value;
        httpClientOptions.RecordException.Should().BeFalse();
        httpClientOptions.EnrichWithException.Should().NotBeNull();
    }
}
