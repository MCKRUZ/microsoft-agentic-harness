using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Presentation.Common.Extensions;
using Presentation.Common.Helpers;
using Presentation.Common.Hosting;
using Xunit;

namespace Presentation.Common.Tests.Hosting;

/// <summary>
/// Regression tests for the <see cref="IHostEnvironment"/> registration in
/// <see cref="IServiceCollectionExtensions.BuildGlobalSolutionServices"/>.
/// </summary>
/// <remarks>
/// Reproduces GitHub issues #19 and #64: console-style hosts (ConsoleUI, EvalRunner,
/// FoundryHost) compose a bare <see cref="ServiceCollection"/> with no <c>IHost</c>, so
/// <see cref="IHostEnvironment"/> was never registered. Services that hard-inject it —
/// notably <c>AutonomyDecisionEvaluator</c> in the MediatR tool-permission behavior — then
/// failed to activate, surfacing only as a sanitized "internal error during the agent turn".
/// </remarks>
public sealed class HarnessHostEnvironmentTests
{
    [Fact]
    public void BuildGlobalSolutionServices_RegistersHostEnvironment_ForConsoleStyleHosts()
    {
        // Mirrors the console composition: a bare ServiceCollection, no IHost.
        var services = new ServiceCollection();

        services.BuildGlobalSolutionServices(new AppConfig());

        services.Any(d => d.ServiceType == typeof(IHostEnvironment))
            .Should().BeTrue("services that hard-inject IHostEnvironment must resolve in console hosts");
    }

    [Fact]
    public void BuildGlobalSolutionServices_DoesNotClobberExistingHostEnvironment()
    {
        // A web host registers its real IHostEnvironment before GetServices() runs.
        var services = new ServiceCollection();
        var webEnvironment = new HarnessHostEnvironment { EnvironmentName = "Staging" };
        services.AddSingleton<IHostEnvironment>(webEnvironment);

        services.BuildGlobalSolutionServices(new AppConfig());

        services.Count(d => d.ServiceType == typeof(IHostEnvironment))
            .Should().Be(1, "TryAddSingleton must not register a second IHostEnvironment");
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHostEnvironment>()
            .Should().BeSameAs(webEnvironment, "the host's real environment must win");
    }

    [Fact]
    public void HarnessHostEnvironment_ReportsConfiguredEnvironmentName()
    {
        var environment = new HarnessHostEnvironment();

        environment.EnvironmentName.Should().Be(AppConfigHelper.GetEnvironmentName());
        environment.EnvironmentName.Should().NotBeNullOrEmpty();
        environment.ApplicationName.Should().NotBeNullOrEmpty();
        environment.ContentRootPath.Should().NotBeNullOrEmpty();
        environment.ContentRootFileProvider.Should().NotBeNull();
    }
}
