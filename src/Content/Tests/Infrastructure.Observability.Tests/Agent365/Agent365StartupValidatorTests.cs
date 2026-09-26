using System.Reflection;
using Application.Common.Interfaces.Common;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.Observability.Agent365;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.Observability.Tests.Agent365;

/// <summary>
/// Tests for <see cref="Agent365StartupValidator"/>, which refuses to boot a host that has enabled
/// Agent 365 export somewhere the exporter cannot actually be wired, whose baggage-egress policy has
/// been undone since composition, or whose offline-storage directory cannot be secured.
/// </summary>
/// <remarks>
/// The failure this guards is silent by nature: on a host that is not on the web-telemetry path the
/// exporter is simply never attached, so the feature costs its overhead and exports nothing, with no
/// error to follow. The symptom — an agent that never appears in the tenant's inventory — is identical
/// to a licensing or consent problem, which sends whoever debugs it somewhere else entirely.
/// </remarks>
[Collection(GlobalPropagatorCollection.Name)]
public class Agent365StartupValidatorTests : IDisposable
{
    private const string AgentAppId = "11111111-1111-1111-1111-111111111111";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";

    private readonly OpenTelemetry.Context.Propagation.TextMapPropagator _originalPropagator =
        OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator;

    /// <summary>
    /// Establishes the baseline every test in this class implicitly relies on: a trace-context-only
    /// propagator, matching what real composition already did before this validator ever runs (#738's
    /// re-assertion is a defence-in-depth check on top of AddOpenTelemetry, not a substitute for it).
    /// Captured <em>after</em> <see cref="_originalPropagator"/>, so <see cref="Dispose"/> still restores
    /// whatever this class found the propagator to be, not this baseline. Individual tests for the
    /// re-assertion itself override this baseline deliberately, to exercise the case it exists to catch.
    /// </summary>
    public Agent365StartupValidatorTests() =>
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(
            new OpenTelemetry.Context.Propagation.TraceContextPropagator());

    /// <summary>
    /// The entry assembly of the running test host. The validator branches on exactly this value, so a
    /// test that wants to simulate a correctly-listed host has to list the real one.
    /// </summary>
    private static string EntryAssemblyName =>
        Assembly.GetEntryAssembly()?.GetName().Name ?? "UnknownService";

    private static Agent365StartupValidator Build(
        Action<AppConfig> configure,
        IOwnerOnlyDirectoryCreator? directoryCreator = null)
    {
        var appConfig = new AppConfig();
        configure(appConfig);

        return new Agent365StartupValidator(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            NullLogger<Agent365StartupValidator>.Instance,
            directoryCreator ?? Mock.Of<IOwnerOnlyDirectoryCreator>());
    }

    private static void ConfigureListedAndEnabled(AppConfig c)
    {
        c.Observability.WebTelemetryProjects.Add(EntryAssemblyName);
        c.Observability.Exporters.Agent365.Enabled = true;
        c.Observability.Exporters.Agent365.AgentAppId = AgentAppId;
        c.Observability.Exporters.Agent365.TenantId = TenantId;
    }

    /// <summary>Restores the process-global propagator so a later test never observes this one's swap.</summary>
    public void Dispose() =>
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(_originalPropagator);

    [Fact]
    public async Task Disabled_DoesNotThrowEvenOnANonWebTelemetryHost()
    {
        // The overwhelmingly common case: the feature is off, and this validator must be invisible.
        var validator = Build(c => c.Observability.Exporters.Agent365.Enabled = false);

        var act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnabledOnAHostThatCannotExport_ThrowsNamingTheHostAndTheFix()
    {
        // The test host is not in WebTelemetryProjects, which is precisely the misconfiguration.
        var validator = Build(c =>
        {
            c.Observability.Exporters.Agent365.Enabled = true;
            c.Observability.Exporters.Agent365.AgentAppId = AgentAppId;
            c.Observability.Exporters.Agent365.TenantId = TenantId;
        });

        var act = () => validator.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();

        // The message has to carry both the offending host and the remedy: the person reading it is
        // looking at an agent missing from a dashboard, with no other signal to go on.
        thrown.And.Message.Should().Contain(EntryAssemblyName);
        thrown.And.Message.Should().Contain("WebTelemetryProjects");
    }

    [Fact]
    public async Task EnabledOnAListedHost_DoesNotThrow()
    {
        var validator = Build(ConfigureListedAndEnabled);

        var act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HostMatching_IsCaseInsensitive()
    {
        // WebTelemetryProjects is operator-typed, and the branch in AddOpenTelemetry that decides the
        // host's telemetry shape matches case-insensitively. If this validator were stricter it would
        // refuse to boot a host that does in fact export correctly.
        var validator = Build(c =>
        {
            c.Observability.WebTelemetryProjects.Add(EntryAssemblyName.ToUpperInvariant());
            c.Observability.Exporters.Agent365.Enabled = true;
            c.Observability.Exporters.Agent365.AgentAppId = AgentAppId;
            c.Observability.Exporters.Agent365.TenantId = TenantId;
        });

        var act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // --- Baggage-egress re-assertion (#738) ----------------------------------------

    [Fact]
    public async Task EnabledWithTraceContextOnlyPropagator_DoesNotThrow()
    {
        // The expected steady state, already established by this class's own constructor — restated
        // explicitly here as its own test rather than left implicit in every other test's baseline.
        var validator = Build(ConfigureListedAndEnabled);

        var act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnabledWithACompositePropagatorCarryingBaggage_ThrowsNamingTheCause()
    {
        // Simulates something that ran after AddOpenTelemetry re-registering a composite propagator —
        // a consumer's own Startup code, or a library that calls SetDefaultTextMapPropagator itself.
        // This is the regression the re-assertion exists to catch: without it, this host would boot
        // successfully while silently leaking tenant/agent/conversation ids on every outbound call.
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(
            new OpenTelemetry.Context.Propagation.CompositeTextMapPropagator(
            [
                new OpenTelemetry.Context.Propagation.TraceContextPropagator(),
                new OpenTelemetry.Context.Propagation.BaggagePropagator(),
            ]));

        var validator = Build(ConfigureListedAndEnabled);

        var act = () => validator.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.And.Message.Should().Contain("baggage");
        thrown.And.Message.Should().Contain("PropagateBaggage");
    }

    [Fact]
    public async Task EnabledWithPropagateBaggageTrue_AndACompositePropagator_DoesNotThrow()
    {
        // Found by code review: without gating the throw on PropagateBaggage, this exact combination —
        // the documented, tested opt-in to cross-process baggage, together with Agent 365 — could never
        // boot. A composite propagator here is not a regression to catch; it is precisely what
        // AddOpenTelemetry was configured to install.
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(
            new OpenTelemetry.Context.Propagation.CompositeTextMapPropagator(
            [
                new OpenTelemetry.Context.Propagation.TraceContextPropagator(),
                new OpenTelemetry.Context.Propagation.BaggagePropagator(),
            ]));

        var validator = Build(c =>
        {
            ConfigureListedAndEnabled(c);
            c.Observability.PropagateBaggage = true;
        });

        var act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisabledWithACompositePropagatorCarryingBaggage_DoesNotThrow()
    {
        // The re-assertion is scoped to hosts that enabled Agent 365 — a host that has not opted in
        // has made no claim about baggage egress for this validator to hold it to.
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(
            new OpenTelemetry.Context.Propagation.CompositeTextMapPropagator(
            [
                new OpenTelemetry.Context.Propagation.TraceContextPropagator(),
                new OpenTelemetry.Context.Propagation.BaggagePropagator(),
            ]));

        var validator = Build(c => c.Observability.Exporters.Agent365.Enabled = false);

        var act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // --- Offline-storage directory enforcement (#738) ------------------------------

    [Fact]
    public async Task OfflineStorageEnabled_CreatesTheDirectoryOwnerOnly()
    {
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(
            new OpenTelemetry.Context.Propagation.TraceContextPropagator());

        var directoryCreator = new Mock<IOwnerOnlyDirectoryCreator>();
        var validator = Build(
            c =>
            {
                ConfigureListedAndEnabled(c);
                c.Observability.Exporters.Agent365.EnableOfflineStorage = true;
                c.Observability.Exporters.Agent365.OfflineStorageDirectory = "/var/spool/agent365";
            },
            directoryCreator.Object);

        await validator.StartAsync(CancellationToken.None);

        directoryCreator.Verify(
            d => d.Create("/var/spool/agent365", It.IsAny<Microsoft.Extensions.Logging.ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task OfflineStorageDisabled_NeverCallsTheDirectoryCreator()
    {
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(
            new OpenTelemetry.Context.Propagation.TraceContextPropagator());

        var directoryCreator = new Mock<IOwnerOnlyDirectoryCreator>();
        var validator = Build(ConfigureListedAndEnabled, directoryCreator.Object);

        await validator.StartAsync(CancellationToken.None);

        directoryCreator.Verify(
            d => d.Create(It.IsAny<string>(), It.IsAny<Microsoft.Extensions.Logging.ILogger?>()),
            Times.Never);
    }

    [Fact]
    public async Task OfflineStorageDirectoryCannotBeSecured_ThrowsNamingTheDirectory()
    {
        // The directory creator throws IOException when it cannot confirm owner-only permissions
        // (a bind-mounted volume owned by a different user, for example). This host asked for offline
        // storage, and content that can include prompts and tool arguments must not land in a
        // directory this process cannot secure — so the harness refuses to boot rather than proceed.
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(
            new OpenTelemetry.Context.Propagation.TraceContextPropagator());

        var directoryCreator = new Mock<IOwnerOnlyDirectoryCreator>();
        directoryCreator
            .Setup(d => d.Create(It.IsAny<string>(), It.IsAny<Microsoft.Extensions.Logging.ILogger?>()))
            .Throws(new IOException("permission denied"));

        var validator = Build(
            c =>
            {
                ConfigureListedAndEnabled(c);
                c.Observability.Exporters.Agent365.EnableOfflineStorage = true;
                c.Observability.Exporters.Agent365.OfflineStorageDirectory = "/var/spool/agent365";
            },
            directoryCreator.Object);

        var act = () => validator.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.And.Message.Should().Contain("/var/spool/agent365");
        thrown.And.InnerException.Should().BeOfType<IOException>();
    }
}
