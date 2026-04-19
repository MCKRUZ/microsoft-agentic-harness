using System.Net;
using System.Net.Http.Headers;
using CorrelationId;
using CorrelationId.Abstractions;
using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.APIAccess.Handlers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Handlers;

/// <summary>
/// Integration tests for the HTTP handler pipeline, testing handlers in combination
/// with real <see cref="HttpMessageHandler"/> chains.
/// </summary>
public sealed class HttpHandlerPipelineTests
{
    /// <summary>Concrete subclass for testing since HttpClientConfig is abstract.</summary>
    private sealed class TestHttpClientConfig : HttpClientConfig;

    /// <summary>
    /// A test handler that captures the final outbound request for inspection.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    // -- UserAgent + Logging pipeline --

    [Fact]
    public async Task Pipeline_UserAgentAndLogging_BothApply()
    {
        var capturing = new CapturingHandler();
        var logging = new LoggingDelegatingHandler(Mock.Of<ILogger<LoggingDelegatingHandler>>())
        {
            InnerHandler = capturing
        };
        var userAgent = new UserAgentDelegatingHandler("TestApp", "2.0.0")
        {
            InnerHandler = logging
        };

        using var client = new HttpClient(userAgent);
        await client.GetAsync("http://localhost/test");

        capturing.CapturedRequest.Should().NotBeNull();
        capturing.CapturedRequest!.Headers.UserAgent.ToString()
            .Should().Contain("TestApp/2.0.0");
    }

    // -- CorrelationId + UserAgent + Logging full pipeline --

    [Fact]
    public async Task Pipeline_FullChain_AppliesAllHeaders()
    {
        var correlationId = "test-correlation-123";
        var contextAccessor = new Mock<ICorrelationContextAccessor>();
        contextAccessor.Setup(a => a.CorrelationContext)
            .Returns(new CorrelationContext(correlationId, "X-Correlation-ID"));

        var correlationOptions = Options.Create(new CorrelationIdOptions
        {
            RequestHeader = "X-Correlation-ID"
        });

        var capturing = new CapturingHandler();
        var logging = new LoggingDelegatingHandler(Mock.Of<ILogger<LoggingDelegatingHandler>>())
        {
            InnerHandler = capturing
        };
        var userAgent = new UserAgentDelegatingHandler("TestApp", "1.0.0")
        {
            InnerHandler = logging
        };
        var correlation = new CorrelationIdDelegatingHandler(contextAccessor.Object, correlationOptions)
        {
            InnerHandler = userAgent
        };

        using var client = new HttpClient(correlation);
        await client.GetAsync("http://localhost/test");

        var request = capturing.CapturedRequest!;
        request.Headers.GetValues("X-Correlation-ID").Should().ContainSingle(correlationId);
        request.Headers.UserAgent.ToString().Should().Contain("TestApp/1.0.0");
    }

    // -- CorrelationId skips when header already present --

    [Fact]
    public async Task CorrelationIdHandler_HeaderAlreadyPresent_DoesNotOverwrite()
    {
        var contextAccessor = new Mock<ICorrelationContextAccessor>();
        contextAccessor.Setup(a => a.CorrelationContext)
            .Returns(new CorrelationContext("new-id", "X-Correlation-ID"));

        var correlationOptions = Options.Create(new CorrelationIdOptions
        {
            RequestHeader = "X-Correlation-ID"
        });

        var capturing = new CapturingHandler();
        var handler = new CorrelationIdDelegatingHandler(contextAccessor.Object, correlationOptions)
        {
            InnerHandler = capturing
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
        request.Headers.Add("X-Correlation-ID", "original-id");
        await client.SendAsync(request);

        capturing.CapturedRequest!.Headers.GetValues("X-Correlation-ID")
            .Should().ContainSingle("original-id");
    }

    // -- CorrelationId skips when context is null --

    [Fact]
    public async Task CorrelationIdHandler_NullContext_DoesNotAddHeader()
    {
        var contextAccessor = new Mock<ICorrelationContextAccessor>();
        contextAccessor.Setup(a => a.CorrelationContext).Returns((CorrelationContext?)null);

        var correlationOptions = Options.Create(new CorrelationIdOptions
        {
            RequestHeader = "X-Correlation-ID"
        });

        var capturing = new CapturingHandler();
        var handler = new CorrelationIdDelegatingHandler(contextAccessor.Object, correlationOptions)
        {
            InnerHandler = capturing
        };

        using var client = new HttpClient(handler);
        await client.GetAsync("http://localhost/test");

        capturing.CapturedRequest!.Headers.Contains("X-Correlation-ID").Should().BeFalse();
    }

    // -- DefaultHttpClientHandler --

    [Fact]
    public void DefaultHttpClientHandler_Default_EnablesDecompression()
    {
        var handler = new DefaultHttpClientHandler();

        handler.AutomaticDecompression.Should().HaveFlag(DecompressionMethods.Brotli);
        handler.AutomaticDecompression.Should().HaveFlag(DecompressionMethods.Deflate);
        handler.AutomaticDecompression.Should().HaveFlag(DecompressionMethods.GZip);
    }

    [Fact]
    public void DefaultHttpClientHandler_DevelopmentConfig_BypassesCertValidation()
    {
        var config = new TestHttpClientConfig { Environment = "Development" };

        var handler = new DefaultHttpClientHandler(config);

        handler.ServerCertificateCustomValidationCallback.Should().NotBeNull();
        handler.ServerCertificateCustomValidationCallback!(null!, null!, null!, default)
            .Should().BeTrue();
    }

    [Fact]
    public void DefaultHttpClientHandler_ProductionConfig_NoCertBypass()
    {
        var config = new TestHttpClientConfig { Environment = "Production" };

        var handler = new DefaultHttpClientHandler(config);

        handler.ServerCertificateCustomValidationCallback.Should().BeNull();
    }

    [Fact]
    public void DefaultHttpClientHandler_NullConfig_ThrowsArgumentNullException()
    {
        var act = () => new DefaultHttpClientHandler(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- UserAgentDelegatingHandler constructor overloads --

    [Fact]
    public void UserAgentDelegatingHandler_ExplicitValues_SetsCorrectHeaders()
    {
        var handler = new UserAgentDelegatingHandler("MyApp", "3.5.0");

        handler.UserAgentValues.Should().HaveCount(2);
        handler.UserAgentValues[0].Product!.Name.Should().Be("MyApp");
        handler.UserAgentValues[0].Product!.Version.Should().Be("3.5.0");
    }

    [Fact]
    public void UserAgentDelegatingHandler_SpacesInName_ReplacedWithHyphens()
    {
        var handler = new UserAgentDelegatingHandler("My App Name", "1.0.0");

        handler.UserAgentValues[0].Product!.Name.Should().Be("My-App-Name");
    }

    [Fact]
    public void UserAgentDelegatingHandler_CustomHeaderValues_ArePreserved()
    {
        var values = new List<ProductInfoHeaderValue>
        {
            new("Custom", "1.0"),
            new("(Test)")
        };

        var handler = new UserAgentDelegatingHandler(values);

        handler.UserAgentValues.Should().HaveCount(2);
        handler.UserAgentValues[0].Product!.Name.Should().Be("Custom");
    }

    [Fact]
    public void UserAgentDelegatingHandler_NullName_ThrowsArgumentNullException()
    {
        var act = () => new UserAgentDelegatingHandler(null!, "1.0");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UserAgentDelegatingHandler_NullVersion_ThrowsArgumentNullException()
    {
        var act = () => new UserAgentDelegatingHandler("App", null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
