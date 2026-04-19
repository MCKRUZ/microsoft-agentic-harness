using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.APIAccess.Common.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Common.Extensions;

/// <summary>
/// Integration tests for <see cref="IServiceCollectionExtensions"/> covering
/// Kestrel, API versioning, rate limiter, CORS, and Swagger registration.
/// </summary>
public sealed class IServiceCollectionExtensionsTests
{
    // -- AddCustomKestrelServerOptions --

    [Fact]
    public void AddCustomKestrelServerOptions_ConfiguresMaxConcurrentConnections()
    {
        var services = new ServiceCollection();
        services.AddCustomKestrelServerOptions();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        options.Limits.MaxConcurrentConnections.Should().Be(100);
    }

    [Fact]
    public void AddCustomKestrelServerOptions_ConfiguresMaxRequestBodySize()
    {
        var services = new ServiceCollection();
        services.AddCustomKestrelServerOptions();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        options.Limits.MaxRequestBodySize.Should().Be(10 * 1024 * 1024);
    }

    [Fact]
    public void AddCustomKestrelServerOptions_ConfiguresRequestHeadersTimeout()
    {
        var services = new ServiceCollection();
        services.AddCustomKestrelServerOptions();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        options.Limits.RequestHeadersTimeout.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void AddCustomKestrelServerOptions_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddCustomKestrelServerOptions();

        result.Should().BeSameAs(services);
    }

    // -- AddCustomApiVersioning --

    [Fact]
    public void AddCustomApiVersioning_DoesNotThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddCustomApiVersioning();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddCustomApiVersioning_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddCustomApiVersioning();

        result.Should().BeSameAs(services);
    }

    // -- AddCustomSwaggerGen --

    [Fact]
    public void AddCustomSwaggerGen_OpenApiDisabled_DoesNotRegisterSwagger()
    {
        var services = new ServiceCollection();
        var config = new HttpConfig
        {
            HttpSwagger = new HttpSwaggerConfig { OpenApiEnabled = false }
        };

        services.AddCustomSwaggerGen(config);

        services.Any(d => d.ServiceType.Name.Contains("Swagger")).Should().BeFalse();
    }

    [Fact]
    public void AddCustomSwaggerGen_NullConfig_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddCustomSwaggerGen(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddCustomSwaggerGen_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var config = new HttpConfig();

        var result = services.AddCustomSwaggerGen(config);

        result.Should().BeSameAs(services);
    }

    // -- AddCustomRateLimiter --

    [Fact]
    public void AddCustomRateLimiter_DoesNotThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddCustomRateLimiter();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddCustomRateLimiter_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddCustomRateLimiter();

        result.Should().BeSameAs(services);
    }

    // -- AddCustomCorsPolicy --

    [Fact]
    public void AddCustomCorsPolicy_RegistersCorsServices()
    {
        var services = new ServiceCollection();
        var config = new HttpConfig { CorsAllowedOrigins = "http://localhost:3000" };

        services.AddCustomCorsPolicy(config);

        services.Any(d => d.ServiceType.Name.Contains("Cors")).Should().BeTrue();
    }

    [Fact]
    public void AddCustomCorsPolicy_NullConfig_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddCustomCorsPolicy(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddCustomCorsPolicy_EmptyOrigins_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var config = new HttpConfig { CorsAllowedOrigins = "" };

        var act = () => services.AddCustomCorsPolicy(config);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddCustomCorsPolicy_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var config = new HttpConfig();

        var result = services.AddCustomCorsPolicy(config);

        result.Should().BeSameAs(services);
    }
}
