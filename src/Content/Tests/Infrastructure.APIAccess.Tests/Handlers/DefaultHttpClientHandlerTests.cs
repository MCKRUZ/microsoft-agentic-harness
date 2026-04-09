using System.Net;
using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.APIAccess.Handlers;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Handlers;

public class DefaultHttpClientHandlerTests
{
    [Fact]
    public void Constructor_Default_EnablesBrotliDecompression()
    {
        using var handler = new DefaultHttpClientHandler();

        handler.AutomaticDecompression.Should().HaveFlag(DecompressionMethods.Brotli);
    }

    [Fact]
    public void Constructor_Default_EnablesDeflateDecompression()
    {
        using var handler = new DefaultHttpClientHandler();

        handler.AutomaticDecompression.Should().HaveFlag(DecompressionMethods.Deflate);
    }

    [Fact]
    public void Constructor_Default_EnablesGZipDecompression()
    {
        using var handler = new DefaultHttpClientHandler();

        handler.AutomaticDecompression.Should().HaveFlag(DecompressionMethods.GZip);
    }

    [Fact]
    public void Constructor_Default_DoesNotSetCertificateCallback()
    {
        using var handler = new DefaultHttpClientHandler();

        handler.ServerCertificateCustomValidationCallback.Should().BeNull();
    }

    [Fact]
    public void Constructor_DevelopmentConfig_BypassesCertValidation()
    {
        var config = new TestHttpClientConfig { Environment = "Development" };

        using var handler = new DefaultHttpClientHandler(config);

        handler.ServerCertificateCustomValidationCallback.Should().NotBeNull();
        handler.ServerCertificateCustomValidationCallback!(null!, null!, null!, default)
            .Should().BeTrue();
    }

    [Fact]
    public void Constructor_ProductionConfig_DoesNotBypassCertValidation()
    {
        var config = new TestHttpClientConfig { Environment = "Production" };

        using var handler = new DefaultHttpClientHandler(config);

        handler.ServerCertificateCustomValidationCallback.Should().BeNull();
    }

    [Fact]
    public void Constructor_DevelopmentConfig_StillEnablesDecompression()
    {
        var config = new TestHttpClientConfig { Environment = "Development" };

        using var handler = new DefaultHttpClientHandler(config);

        var expected = DecompressionMethods.Brotli
                     | DecompressionMethods.Deflate
                     | DecompressionMethods.GZip;
        handler.AutomaticDecompression.Should().Be(expected);
    }

    [Fact]
    public void Constructor_NullConfig_ThrowsArgumentNullException()
    {
        var act = () => new DefaultHttpClientHandler(null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("clientConfig");
    }

    private sealed class TestHttpClientConfig : HttpClientConfig
    {
    }
}
