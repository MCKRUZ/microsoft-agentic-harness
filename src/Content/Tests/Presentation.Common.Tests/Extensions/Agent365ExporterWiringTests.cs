using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Proves the Agent 365 exporter is actually wired into the real telemetry composition — and,
/// equally, that it registers nothing when it has not been opted into.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because of a defect shape this repository has shipped repeatedly: a control
/// that is written, unit-tested and documented as enforcing, but which nothing ever invokes. The
/// question it answers is the one that matters — <em>which single line, if deleted, restores the
/// unguarded behaviour?</em> That line is the <c>AddAgent365Exporter(appConfig)</c> call in
/// <c>AddWebTelemetry</c>, and <see cref="EnabledConfig_RegistersTheAgent365Exporter"/> fails if it
/// is removed. A validator or an extension method passing its own tests proves nothing about
/// whether a host ever reaches it.
/// </para>
/// <para>
/// <c>AddWebTelemetry</c> is called directly rather than through <c>AddOpenTelemetry(appConfig)</c>.
/// That entry point branches on whether the entry assembly is listed in
/// <c>Observability:WebTelemetryProjects</c>, and in a test process the entry assembly is the test
/// host — so the web branch is unreachable from it. The method is <c>internal</c> precisely so tests
/// can exercise the branch a real web host takes.
/// </para>
/// </remarks>
[Collection(GlobalPropagatorCollection.Name)]
public class Agent365ExporterWiringTests : IDisposable
{
    // Enabling the exporter swaps the process-wide propagator, which would otherwise persist for every
    // later test in the same run and could destabilise anything that depends on context propagation.
    // Captured before each test and restored after, so the suite leaves global state as it found it.
    private readonly OpenTelemetry.Context.Propagation.TextMapPropagator _originalPropagator =
        OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator;

    /// <inheritdoc />
    public void Dispose() => OpenTelemetry.Sdk.SetDefaultTextMapPropagator(_originalPropagator);

    private const string AgentAppId = "11111111-1111-1111-1111-111111111111";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";

    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static AppConfig ConfigWithAgent365(bool enabled)
    {
        var config = new AppConfig();
        config.Observability.Exporters.Agent365.Enabled = enabled;
        config.Observability.Exporters.Agent365.AgentAppId = AgentAppId;
        config.Observability.Exporters.Agent365.TenantId = TenantId;
        return config;
    }

    [Fact]
    public void EnabledConfig_RegistersTheAgent365Exporter()
    {
        // Selecting the Agent 365 target makes the distro register its own services into the
        // container, so their presence is evidence the vendor pipeline was actually composed — not
        // merely that our extension method returned without throwing.
        var services = BaseServices();

        services.AddWebTelemetry(ConfigWithAgent365(enabled: true));

        services.Should().Contain(
            d => IsAgent365Service(d),
            "enabling Observability:Exporters:Agent365 must compose the vendor's Agent 365 pipeline; "
            + "if this fails, the AddAgent365Exporter call in AddWebTelemetry has been removed and "
            + "agent activity silently stops reaching the tenant's control plane");
    }

    [Fact]
    public void DisabledConfig_RegistersNothingForAgent365()
    {
        // Default-off must mean genuinely inert, not "registered but idle". A consumer who has not
        // opted in should carry none of the exporter's machinery — including its background replay
        // loop and on-disk store.
        var services = BaseServices();

        services.AddWebTelemetry(ConfigWithAgent365(enabled: false));

        services.Should().NotContain(
            d => IsAgent365Service(d),
            "a host that has not enabled Agent 365 must register none of the exporter's services");
    }

    [Fact]
    public void DefaultConfig_DoesNotEnableAgent365()
    {
        // Guards the default itself: a stray initializer flipping Enabled to true would opt every
        // downstream consumer of this template into exporting agent activity to a tenant service.
        var services = BaseServices();

        services.AddWebTelemetry(new AppConfig());

        services.Should().NotContain(d => IsAgent365Service(d));
    }

    [Fact]
    public void EnabledConfig_StillRegistersTheHarnessOwnTelemetryPipeline()
    {
        // The distro's headline entry point can take over exporters and instrumentation. This asserts
        // the narrowing worked: the harness's own OTel registration survives alongside Agent 365, so
        // enabling Agent 365 does not quietly displace the existing observability stack.
        var services = BaseServices();

        services.AddWebTelemetry(ConfigWithAgent365(enabled: true));

        services.Should().Contain(
            d => d.ServiceType == typeof(OpenTelemetry.Trace.TracerProvider),
            "the harness's own tracer provider must still be registered when Agent 365 is enabled");
    }

    [Fact]
    public void EnabledConfig_StopsPropagatingBaggageOutOfTheProcess()
    {
        // Attribution is published as OpenTelemetry baggage, and the default propagator serialises
        // baggage into an HTTP header that the HTTP-client instrumentation attaches to every outbound
        // call — so without this, enabling agent governance would send the customer's tenant id, agent
        // app id, blueprint id and conversation id to LLM providers, third-party MCP servers and
        // web-fetch targets. It also stops a caller-supplied baggage header being extracted and
        // stamped onto spans as attacker-chosen agent attribution.
        var services = BaseServices();

        services.AddWebTelemetry(ConfigWithAgent365(enabled: true));

        // Asserted on the trace-context fields surviving rather than on the concrete propagator type:
        // what matters is that distributed tracing still works while baggage no longer crosses the
        // boundary, and the fields are the observable contract.
        var fields = OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator.Fields;

        fields.Should().Contain("traceparent", "distributed tracing must keep working");
        fields.Should().NotContain("baggage", "baggage must not leave the process");
    }

    // Matched by name rather than by CLR type on purpose: the vendor's Agent 365 service types are
    // internal to its assembly, so a typed reference will not compile. What matters for this suite is
    // the observable fact that enabling the feature adds Agent 365 services to the container and
    // disabling it adds none — which a name match establishes without reaching into internals.
    private static bool IsAgent365Service(ServiceDescriptor descriptor)
        => (descriptor.ServiceType.FullName ?? string.Empty).Contains("Agent365", StringComparison.Ordinal)
            || (descriptor.ImplementationType?.FullName ?? string.Empty).Contains("Agent365", StringComparison.Ordinal);
}
