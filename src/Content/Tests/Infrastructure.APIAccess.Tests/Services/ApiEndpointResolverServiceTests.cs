using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.APIAccess.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Services;

public class ApiEndpointResolverServiceTests
{
    private readonly Mock<IOptionsMonitor<HttpConfig>> _httpConfigMock;
    private readonly ILogger<ApiEndpointResolverService> _logger;
    private readonly IMemoryCache _cache;

    public ApiEndpointResolverServiceTests()
    {
        _httpConfigMock = new Mock<IOptionsMonitor<HttpConfig>>();
        _httpConfigMock.Setup(m => m.CurrentValue).Returns(new HttpConfig());
        _logger = NullLogger<ApiEndpointResolverService>.Instance;
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    [Fact]
    public void Constructor_NullHttpConfig_ThrowsArgumentNullException()
    {
        var act = () => new ApiEndpointResolverService(null!, _logger, _cache);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("httpConfig");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new ApiEndpointResolverService(_httpConfigMock.Object, null!, _cache);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_NullCache_ThrowsArgumentNullException()
    {
        var act = () => new ApiEndpointResolverService(
            _httpConfigMock.Object, _logger, null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("cache");
    }

    [Fact]
    public void Constructor_ValidArgs_CreatesInstance()
    {
        var service = new ApiEndpointResolverService(_httpConfigMock.Object, _logger, _cache);

        service.Should().NotBeNull();
    }

    [Fact]
    public void GetClientConfig_UnknownSection_ThrowsArgumentException()
    {
        var service = new ApiEndpointResolverService(_httpConfigMock.Object, _logger, _cache);

        var act = () => service.GetClientConfig<TestHttpClientConfig>("UnknownSection");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown configuration section name*UnknownSection*");
    }

    [Fact]
    public void ResolveEndpoint_CachedEndpoint_ReturnsCachedValue()
    {
        var expectedUri = new Uri("https://cached.example.com");
        _cache.Set("endpoint-TestSection", expectedUri, TimeSpan.FromMinutes(5));

        var service = new ApiEndpointResolverService(_httpConfigMock.Object, _logger, _cache);

        var result = service.ResolveEndpoint<TestHttpClientConfig>("TestSection");

        result.Should().Be(expectedUri);
    }

    [Fact]
    public void ResolveEndpoint_NotCached_ThrowsBecauseGetClientConfigThrows()
    {
        var service = new ApiEndpointResolverService(_httpConfigMock.Object, _logger, _cache);

        // ResolveEndpoint calls GetClientConfig which currently always throws
        // because no configuration sections are mapped
        var act = () => service.ResolveEndpoint<TestHttpClientConfig>("AnySectionName");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown configuration section name*");
    }

    [Fact]
    public void ResolveEndpoint_SameKeyCalledTwice_UsesCache()
    {
        var expectedUri = new Uri("https://first-resolve.example.com");
        _cache.Set("endpoint-CacheTest", expectedUri, TimeSpan.FromMinutes(5));

        var service = new ApiEndpointResolverService(_httpConfigMock.Object, _logger, _cache);

        var first = service.ResolveEndpoint<TestHttpClientConfig>("CacheTest");
        var second = service.ResolveEndpoint<TestHttpClientConfig>("CacheTest");

        first.Should().Be(second);
        first.Should().Be(expectedUri);
    }

    private sealed class TestHttpClientConfig : HttpClientConfig
    {
    }
}
