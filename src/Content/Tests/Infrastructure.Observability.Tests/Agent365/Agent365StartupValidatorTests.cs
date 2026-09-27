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
/// Agent 365 export somewhere the exporter cannot actually be wired, or whose offline-storage
/// directory cannot be secured.
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards is silent by nature: on a host that is not on the web-telemetry path the
/// exporter is simply never attached, so the feature costs its overhead and exports nothing, with no
/// error to follow. The symptom — an agent that never appears in the tenant's inventory — is identical
/// to a licensing or consent problem, which sends whoever debugs it somewhere else entirely.
/// </para>
/// <para>
/// The baggage-egress re-assertion this class used to test lives in its own
/// <c>BaggageEgressStartupValidator</c> now (see <c>BaggageEgressStartupValidatorTests</c>) — nesting it
/// behind this validator's Agent-365-specific early return meant a host with Agent 365 disabled got
/// none of that host-wide policy's protection (#738's altitude pass). This class no longer touches the
/// process-global propagator at all, and needs neither <c>GlobalPropagatorCollection</c> nor
/// baseline/restore scaffolding as a result.
/// </para>
/// </remarks>
public class Agent365StartupValidatorTests
{
    private const string AgentAppId = "11111111-1111-1111-1111-111111111111";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";

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

    // --- Offline-storage directory enforcement (#738) ------------------------------

    [Fact]
    public async Task OfflineStorageEnabled_CreatesTheDirectoryOwnerOnly()
    {
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

    [Fact]
    public async Task OfflineStorageDirectoryPathIsMalformed_AlsoThrowsNamingTheDirectory()
    {
        // A code-review round on #738 found the original catch clause listed only IOException and
        // UnauthorizedAccessException — but Directory.CreateDirectory can also throw ArgumentException
        // (invalid characters, reserved device names) or NotSupportedException (a colon mid-path on
        // Windows) for a malformed operator-supplied path. Without this, those exceptions would escape
        // as an opaque crash instead of the same clear boot refusal every other misconfiguration here
        // produces. ArgumentException stands in for that whole class in this test.
        var directoryCreator = new Mock<IOwnerOnlyDirectoryCreator>();
        directoryCreator
            .Setup(d => d.Create(It.IsAny<string>(), It.IsAny<Microsoft.Extensions.Logging.ILogger?>()))
            .Throws(new ArgumentException("illegal characters in path"));

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
        thrown.And.InnerException.Should().BeOfType<ArgumentException>();
    }

    [Fact]
    public async Task OfflineStorageEnabledWithBlankDirectory_ThrowsBeforeCallingTheDirectoryCreator()
    {
        // Defense in depth against Agent365ExporterConfigValidator going unbound — this repo has a
        // documented, repeated history of exactly that (CLAUDE.md's "shipping a control that nothing
        // invokes" entry lists six prior instances). If it ever happens here, this turns an opaque
        // path-parsing exception into the same clear boot refusal every other Agent 365
        // misconfiguration produces.
        var directoryCreator = new Mock<IOwnerOnlyDirectoryCreator>();
        var validator = Build(
            c =>
            {
                ConfigureListedAndEnabled(c);
                c.Observability.Exporters.Agent365.EnableOfflineStorage = true;
                c.Observability.Exporters.Agent365.OfflineStorageDirectory = "   ";
            },
            directoryCreator.Object);

        var act = () => validator.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.And.Message.Should().Contain("OfflineStorageDirectory");
        directoryCreator.Verify(
            d => d.Create(It.IsAny<string>(), It.IsAny<Microsoft.Extensions.Logging.ILogger?>()),
            Times.Never);
    }
}
