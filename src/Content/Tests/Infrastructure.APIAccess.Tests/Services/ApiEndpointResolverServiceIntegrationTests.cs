using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.APIAccess.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Services;

/// <summary>
/// Integration tests for <see cref="ApiEndpointResolverService"/> covering
/// constructor validation, caching, and endpoint resolution.
/// </summary>
public sealed class ApiEndpointResolverServiceIntegrationTests
{
    /// <summary>Concrete test subclass of the abstract <see cref="HttpClientConfig"/>.</summary>
    private sealed class TestHttpClientConfig : HttpClientConfig;

    private static ApiEndpointResolverService CreateService(
        IOptionsMonitor<HttpConfig>? httpConfig = null,
        IMemoryCache? cache = null)
    {
        var configMock = httpConfig ?? CreateConfigMock();
        var logger = Mock.Of<ILogger<ApiEndpointResolverService>>();
        var memCache = cache ?? new MemoryCache(new MemoryCacheOptions());

        return new ApiEndpointResolverService(configMock, logger, memCache);
    }

    private static IOptionsMonitor<HttpConfig> CreateConfigMock()
    {
        var mock = new Mock<IOptionsMonitor<HttpConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(new HttpConfig());
        return mock.Object;
    }

    // -- Constructor validation --

    [Fact]
    public void Constructor_NullHttpConfig_ThrowsArgumentNullException()
    {
        var act = () => new ApiEndpointResolverService(
            null!,
            Mock.Of<ILogger<ApiEndpointResolverService>>(),
            new MemoryCache(new MemoryCacheOptions()));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new ApiEndpointResolverService(
            CreateConfigMock(),
            null!,
            new MemoryCache(new MemoryCacheOptions()));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullCache_ThrowsArgumentNullException()
    {
        var act = () => new ApiEndpointResolverService(
            CreateConfigMock(),
            Mock.Of<ILogger<ApiEndpointResolverService>>(),
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- GetClientConfig --

    [Fact]
    public void GetClientConfig_UnknownSection_ThrowsArgumentException()
    {
        var sut = CreateService();

        var act = () => sut.GetClientConfig<TestHttpClientConfig>("UnknownSection");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown configuration section*");
    }

    // -- ResolveEndpoint --

    [Fact]
    public void ResolveEndpoint_UnknownSection_ThrowsArgumentException()
    {
        var sut = CreateService();

        var act = () => sut.ResolveEndpoint<TestHttpClientConfig>("UnknownSection");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown configuration section*");
    }

    // -- Caching behavior --

    [Fact]
    public void ResolveEndpoint_SameSection_UsesCachedResult()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedUri = new Uri("https://cached.example.com");
        cache.Set("endpoint-TestSection", cachedUri);

        var sut = CreateService(cache: cache);

        // Should not throw because it uses the cached value instead of
        // calling GetClientConfig which would throw for unknown sections
        var result = sut.ResolveEndpoint<TestHttpClientConfig>("TestSection");

        result.Should().Be(cachedUri);
    }
}
