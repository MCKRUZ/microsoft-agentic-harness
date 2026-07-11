using Domain.Common.Config.AI.BundleExecution;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.BundleApi.Extensions;
using Presentation.BundleApi.Services;
using Xunit;

namespace Presentation.BundleApi.Tests;

/// <summary>
/// Unit tests for the bundle API's fail-closed authentication wiring — the security-critical decision ladder
/// in <see cref="BundleApiServiceCollectionExtensions.AddBundleApiAuthentication"/>.
/// </summary>
public sealed class BundleApiAuthenticationTests
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
        var act = () => NewServices().AddBundleApiAuthentication(new BundleApiAuthConfig());

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

        var act = () => NewServices().AddBundleApiAuthentication(auth);

        act.Should().Throw<InvalidOperationException>().WithMessage("*contradictory*");
    }

    [Theory]
    [InlineData("tenant", null)]
    [InlineData(null, "client")]
    public void HalfConfiguredScheme_Throws_FailClosed(string? tenantId, string? clientId)
    {
        var auth = new BundleApiAuthConfig { TenantId = tenantId, ClientId = clientId };

        var act = () => NewServices().AddBundleApiAuthentication(auth);

        act.Should().Throw<InvalidOperationException>().WithMessage("*half-configured*");
    }

    [Fact]
    public void AnonymousOptIn_RegistersSyntheticAuth_AndStartupWarning()
    {
        var services = NewServices();

        services.AddBundleApiAuthentication(new BundleApiAuthConfig { AllowAnonymous = true });

        services.Should().Contain(d => d.ImplementationType == typeof(BundleApiAnonymousModeStartupWarning),
            "the anonymous open-door state must announce itself loudly at startup");

        // AddAuthentication ran (synthetic handler), so [Authorize] endpoints are reachable in anonymous mode.
        using var provider = services.BuildServiceProvider();
        provider.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationService>()
            .Should().NotBeNull();
    }

    [Fact]
    public void ConfiguredEntra_RegistersJwtBearer_WithoutThrowing()
    {
        var services = NewServices();
        var auth = new BundleApiAuthConfig { TenantId = "tenant-id", ClientId = "client-id" };

        var act = () => services.AddBundleApiAuthentication(auth);

        act.Should().NotThrow();
        // A configured scheme installs JwtBearer options for the host's own audience.
        services.Should().Contain(d => d.ServiceType == typeof(IConfigureOptions<JwtBearerOptions>));
    }
}
