using FluentAssertions;
using Infrastructure.APIAccess.Auth.Handlers;
using Infrastructure.APIAccess.Auth.Providers;
using Infrastructure.APIAccess.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.APIAccess.Tests;

/// <summary>
/// Integration tests for <see cref="DependencyInjection.AddInfrastructureApiAccessDependencies"/>
/// verifying correct service registration via a real DI container.
/// </summary>
public sealed class DependencyInjectionTests
{
    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<Domain.Common.Config.Http.HttpConfig>(_ => { });
        return services;
    }

    [Fact]
    public void AddInfrastructureApiAccessDependencies_RegistersApiEndpointResolverService()
    {
        var services = CreateServices();

        services.AddInfrastructureApiAccessDependencies();

        services.Any(d => d.ServiceType == typeof(ApiEndpointResolverService)).Should().BeTrue();
    }

    [Fact]
    public void AddInfrastructureApiAccessDependencies_RegistersPermissionPolicyProvider()
    {
        var services = CreateServices();

        services.AddInfrastructureApiAccessDependencies();

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IAuthorizationPolicyProvider));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(PermissionPolicyProvider));
    }

    [Fact]
    public void AddInfrastructureApiAccessDependencies_RegistersPermissionAuthHandler()
    {
        var services = CreateServices();

        services.AddInfrastructureApiAccessDependencies();

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IAuthorizationHandler));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(PermissionAuthHandler));
    }

    [Fact]
    public void AddInfrastructureApiAccessDependencies_RegistersMemoryCache()
    {
        var services = CreateServices();

        services.AddInfrastructureApiAccessDependencies();

        services.Any(d => d.ServiceType.Name.Contains("MemoryCache")).Should().BeTrue();
    }

    [Fact]
    public void AddInfrastructureApiAccessDependencies_ReturnsSameServiceCollection()
    {
        var services = CreateServices();

        var result = services.AddInfrastructureApiAccessDependencies();

        result.Should().BeSameAs(services);
    }
}
