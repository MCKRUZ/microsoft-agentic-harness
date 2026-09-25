using System.Reflection;
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
/// Agent 365 export somewhere the exporter cannot actually be wired.
/// </summary>
/// <remarks>
/// The failure this guards is silent by nature: on a host that is not on the web-telemetry path the
/// exporter is simply never attached, so the feature costs its overhead and exports nothing, with no
/// error to follow. The symptom — an agent that never appears in the tenant's inventory — is identical
/// to a licensing or consent problem, which sends whoever debugs it somewhere else entirely.
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

    private static Agent365StartupValidator Build(Action<AppConfig> configure)
    {
        var appConfig = new AppConfig();
        configure(appConfig);

        return new Agent365StartupValidator(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            NullLogger<Agent365StartupValidator>.Instance);
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
        var validator = Build(c =>
        {
            c.Observability.WebTelemetryProjects.Add(EntryAssemblyName);
            c.Observability.Exporters.Agent365.Enabled = true;
            c.Observability.Exporters.Agent365.AgentAppId = AgentAppId;
            c.Observability.Exporters.Agent365.TenantId = TenantId;
        });

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
}
