using Domain.Common.Config.AI.BundleExecution;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.ExecutionApi.Extensions;
using Presentation.ExecutionApi.Services;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Unit tests for the bundle API's fail-closed authentication wiring — the security-critical decision ladder
/// in <see cref="ExecutionApiServiceCollectionExtensions.AddExecutionApiAuthentication"/>.
/// </summary>
public sealed class ExecutionApiAuthenticationTests
{
    private static IServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    [Fact]
    public void Unconfigured_WithoutAnonymousOptIn_Throws_FailClosed()
    {
        var act = () => NewServices().AddExecutionApiAuthentication(new BundleApiAuthConfig());

        act.Should().Throw<InvalidOperationException>().WithMessage("*fail-closed*");
    }

    [Fact]
    public void Anonymous_CombinedWithConfiguredScheme_Throws_Contradiction()
    {
        var auth = new BundleApiAuthConfig
        {
            TenantId = "tenant",
            ClientId = "client",
            AllowAnonymous = true
        };

        var act = () => NewServices().AddExecutionApiAuthentication(auth);

        act.Should().Throw<InvalidOperationException>().WithMessage("*contradictory*");
    }

    [Theory]
    [InlineData("tenant", null)]
    [InlineData(null, "client")]
    public void HalfConfiguredScheme_Throws_FailClosed(string? tenantId, string? clientId)
    {
        var auth = new BundleApiAuthConfig { TenantId = tenantId, ClientId = clientId };

        var act = () => NewServices().AddExecutionApiAuthentication(auth);

        act.Should().Throw<InvalidOperationException>().WithMessage("*half-configured*");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AnonymousOptIn_OutsideDevelopment_StartsOnlyWithTheOverride(bool overridden, bool refuses)
    {
        var services = NewServices();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestEnvironment("Production"));
        services.AddExecutionApiAuthentication(
            new BundleApiAuthConfig { AllowAnonymous = true, AllowAnonymousOutsideDevelopment = overridden });
        using var provider = services.BuildServiceProvider();
        var guard = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<Application.Common.Hosting.UnauthenticatedModeStartupGuard>().Single();

        var act = () => guard.StartingAsync(CancellationToken.None);

        if (refuses)
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AllowAnonymousOutsideDevelopment=true*");
        else
            await act.Should().NotThrowAsync();
    }

    [Fact]
    public void AnonymousOptIn_RegistersSyntheticAuth_AndStartupWarning()
    {
        var services = NewServices();

        services.AddExecutionApiAuthentication(new BundleApiAuthConfig { AllowAnonymous = true });

        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestEnvironment("Development"));
        using var provider = services.BuildServiceProvider();
        provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Should().ContainSingle(s => s is Application.Common.Hosting.UnauthenticatedModeStartupGuard,
                "the anonymous open-door state must announce itself at startup, and refuse outside Development (#904)");

        // AddAuthentication ran (synthetic handler), so [Authorize] endpoints are reachable in anonymous mode.
        provider.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationService>()
            .Should().NotBeNull();
    }

    [Fact]
    public void ConfiguredEntra_RegistersJwtBearer_WithoutThrowing()
    {
        var services = NewServices();
        var auth = new BundleApiAuthConfig { TenantId = "tenant-id", ClientId = "client-id" };

        var act = () => services.AddExecutionApiAuthentication(auth);

        act.Should().NotThrow();
        // A configured scheme installs JwtBearer options for the host's own audience.
        services.Should().Contain(d => d.ServiceType == typeof(IConfigureOptions<JwtBearerOptions>));
    }

    private sealed class TestEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
