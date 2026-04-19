using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.APIAccess.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Services;

/// <summary>
/// Extended tests for <see cref="ApiEndpointResolverService"/> covering
/// caching behavior and edge cases.
/// </summary>
public sealed class ApiEndpointResolverServiceExtendedTests
{
    private readonly Mock<IOptionsMonitor<HttpConfig>> _httpConfigMock;
    private readonly IMemoryCache _cache;
    private readonly ApiEndpointResolverService _sut;

    public ApiEndpointResolverServiceExtendedTests()
    {
        _httpConfigMock = new Mock<IOptionsMonitor<HttpConfig>>();
        _httpConfigMock.Setup(m => m.CurrentValue).Returns(new HttpConfig());
        _cache = new MemoryCache(new MemoryCacheOptions());
        _sut = new ApiEndpointResolverService(
            _httpConfigMock.Object,
            NullLogger<ApiEndpointResolverService>.Instance,
            _cache);
    }

    [Fact]
    public void ResolveEndpoint_CachedEndpoint_DoesNotCallGetClientConfig()
    {
        var uri = new Uri("https://cached-endpoint.example.com");
        _cache.Set("endpoint-CachedSection", uri, TimeSpan.FromMinutes(5));

        var result = _sut.ResolveEndpoint<TestHttpClientConfig>("CachedSection");

        result.Should().Be(uri);
    }

    [Fact]
    public void ResolveEndpoint_DifferentCacheKeys_DoNotInterfere()
    {
        var uri1 = new Uri("https://endpoint-1.example.com");
        var uri2 = new Uri("https://endpoint-2.example.com");
        _cache.Set("endpoint-Section1", uri1, TimeSpan.FromMinutes(5));
        _cache.Set("endpoint-Section2", uri2, TimeSpan.FromMinutes(5));

        _sut.ResolveEndpoint<TestHttpClientConfig>("Section1").Should().Be(uri1);
        _sut.ResolveEndpoint<TestHttpClientConfig>("Section2").Should().Be(uri2);
    }

    [Fact]
    public void GetClientConfig_EmptyString_ThrowsArgumentException()
    {
        var act = () => _sut.GetClientConfig<TestHttpClientConfig>("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetClientConfig_AnyString_ThrowsArgumentException()
    {
        var act = () => _sut.GetClientConfig<TestHttpClientConfig>("SomeConfigSection");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown configuration section name*SomeConfigSection*");
    }

    private sealed class TestHttpClientConfig : HttpClientConfig
    {
    }
}
