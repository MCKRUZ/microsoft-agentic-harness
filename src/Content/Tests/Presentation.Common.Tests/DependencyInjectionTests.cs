using Domain.Common.Config.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests;

/// <summary>
/// Integration tests for <see cref="DependencyInjection.AddPresentationCommonDependencies"/>
/// verifying correct service registration via a real DI container.
/// </summary>
public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddPresentationCommonDependencies_RegistersServices()
    {
        var services = new ServiceCollection();
        var httpConfig = new HttpConfig
        {
            CorsAllowedOrigins = "http://localhost:3000",
            HttpSwagger = new HttpSwaggerConfig { OpenApiEnabled = false }
        };

        var act = () => services.AddPresentationCommonDependencies(httpConfig);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddPresentationCommonDependencies_NullHttpConfig_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddPresentationCommonDependencies(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddPresentationCommonDependencies_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var httpConfig = new HttpConfig();

        var result = services.AddPresentationCommonDependencies(httpConfig);

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddPresentationCommonDependencies_RegistersCorsServices()
    {
        var services = new ServiceCollection();
        var httpConfig = new HttpConfig
        {
            CorsAllowedOrigins = "http://localhost:3000"
        };

        services.AddPresentationCommonDependencies(httpConfig);

        services.Any(d => d.ServiceType.Name.Contains("Cors")).Should().BeTrue();
    }

    [Fact]
    public void AddPresentationCommonDependencies_RegistersRateLimiterServices()
    {
        var services = new ServiceCollection();
        var httpConfig = new HttpConfig();

        services.AddPresentationCommonDependencies(httpConfig);

        services.Any(d =>
            d.ServiceType.FullName?.Contains("RateLimiting") == true ||
            d.ServiceType.FullName?.Contains("RateLimiter") == true)
            .Should().BeTrue();
    }
}
