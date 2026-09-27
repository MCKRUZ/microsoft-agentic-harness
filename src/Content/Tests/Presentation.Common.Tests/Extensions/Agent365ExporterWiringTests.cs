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
    // AddOpenTelemetry now mutates BOTH baggage-store propagators (#738's security-review fix), so
    // both are captured and restored.
    private readonly OpenTelemetry.Context.Propagation.TextMapPropagator _originalPropagator =
        OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator;

    private readonly System.Diagnostics.DistributedContextPropagator _originalActivityPropagator =
        System.Diagnostics.DistributedContextPropagator.Current;

    /// <inheritdoc />
    public void Dispose()
    {
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(_originalPropagator);
        System.Diagnostics.DistributedContextPropagator.Current = _originalActivityPropagator;
    }

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
            d => Agent365ServiceMatcher.IsAgent365Service(d),
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
            d => Agent365ServiceMatcher.IsAgent365Service(d),
            "a host that has not enabled Agent 365 must register none of the exporter's services");
    }

    [Fact]
    public void DefaultConfig_DoesNotEnableAgent365()
    {
        // Guards the default itself: a stray initializer flipping Enabled to true would opt every
        // downstream consumer of this template into exporting agent activity to a tenant service.
        var services = BaseServices();

        services.AddWebTelemetry(new AppConfig());

        services.Should().NotContain(d => Agent365ServiceMatcher.IsAgent365Service(d));
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
    public void DefaultConfig_StopsPropagatingBaggageOutOfTheProcess()
    {
        // #738: this is now a host-wide egress policy applied in AddOpenTelemetry, not a side effect
        // of enabling Agent 365 — so it is proved here with Agent 365 untouched, through the same
        // public entry point every host calls. Attribution rides OpenTelemetry baggage, and the
        // default propagator serialises baggage into an HTTP header that the HTTP-client
        // instrumentation attaches to every outbound call — so without this, ANY host that starts
        // using baggage (Agent 365 or otherwise) would send it to LLM providers, third-party MCP
        // servers and web-fetch targets. It also stops a caller-supplied baggage header being
        // extracted and stamped onto spans as attacker-chosen attribution.
        var services = BaseServices();

        services.AddOpenTelemetry(new AppConfig());

        // Asserted on the trace-context fields surviving rather than on the concrete propagator type:
        // what matters is that distributed tracing still works while baggage no longer crosses the
        // boundary, and the fields are the observable contract.
        var fields = OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator.Fields;

        fields.Should().Contain("traceparent", "distributed tracing must keep working");
        fields.Should().NotContain("baggage", "baggage must not leave the process by default");
    }

    [Fact]
    public void DefaultConfig_StopsPropagatingActivityBaggageOutOfTheProcess()
    {
        // A security review of #738 found the ORIGINAL implementation suppressed only the OTel
        // Baggage store — leaving System.Diagnostics.Activity.Baggage, the store this harness's own
        // identity attribution (AgUiRunHandler, ConversationOrchestrator, ExecuteAgentTurnCommandHandler,
        // AgentExecutionContextFactory) actually writes into via Activity.AddBaggage, completely
        // unguarded. This proves the fix: DistributedContextPropagator.Current must also suppress
        // baggage by default.
        var services = BaseServices();

        services.AddOpenTelemetry(new AppConfig());

        var fields = System.Diagnostics.DistributedContextPropagator.Current.Fields;

        fields.Should().Contain("traceparent", "distributed tracing must keep working");
        fields.Should().NotContain("baggage", "Activity baggage must not leave the process by default");
    }

    [Fact]
    public void EnabledConfig_StillSuppressesBaggage_WithPropagateBaggageDefaultFalse()
    {
        // A second code-review round on #738 found no test exercised this exact combination through the
        // real composition path: Agent 365 enabled AND PropagateBaggage left at its default (false).
        // The two DefaultConfig_* tests above prove suppression with Agent 365 untouched;
        // PropagateBaggageTrue_RestoresBaggagePropagation_EvenWithAgent365Enabled proves the opt-in with
        // Agent 365 enabled. Neither proves the one combination this whole feature exists to secure:
        // Agent 365's own tenant/agent/blueprint/conversation ids, suppressed, while the exporter that
        // publishes them is actually wired and running.
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
            ?? "UnknownService";
        var config = ConfigWithAgent365(enabled: true);
        config.Observability.WebTelemetryProjects.Add(entryAssembly);
        var services = BaseServices();

        services.AddOpenTelemetry(config);

        services.Should().Contain(d => Agent365ServiceMatcher.IsAgent365Service(d), "the Agent 365 wiring must actually run");
        var fields = OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator.Fields;
        fields.Should().NotContain("baggage", "OTel baggage must not leave the process by default");
        var activityFields = System.Diagnostics.DistributedContextPropagator.Current.Fields;
        activityFields.Should().NotContain("baggage", "Activity baggage must not leave the process by default");
    }

    [Fact]
    public void PropagateBaggageTrue_RestoresBaggagePropagation_EvenWithAgent365Enabled()
    {
        // The explicit opt-back-in: a host with a deliberate, reviewed reason to use cross-process
        // baggage can still have it, and enabling Agent 365 alongside that choice must not silently
        // re-suppress it — PropagateBaggage is the one flag that decides this, independent of any
        // exporter. Routed through the real public entry point with the test host added to
        // WebTelemetryProjects (matching ValidateOnBuildSweepTests' own approach) so the Agent 365
        // wiring this test names is actually reached, not merely configured.
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
            ?? "UnknownService";
        var config = ConfigWithAgent365(enabled: true);
        config.Observability.PropagateBaggage = true;
        config.Observability.WebTelemetryProjects.Add(entryAssembly);
        var services = BaseServices();

        services.AddOpenTelemetry(config);

        services.Should().Contain(d => Agent365ServiceMatcher.IsAgent365Service(d), "the Agent 365 wiring must actually run");
        var fields = OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator.Fields;
        fields.Should().Contain("baggage", "an explicit opt-in must be honoured");
        var activityFields = System.Diagnostics.DistributedContextPropagator.Current.Fields;
        activityFields.Should().Contain("baggage", "the opt-in must apply to Activity baggage too");
    }
}
