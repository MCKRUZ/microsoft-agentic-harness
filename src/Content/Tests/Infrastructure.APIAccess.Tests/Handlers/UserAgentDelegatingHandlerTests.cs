using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Infrastructure.APIAccess.Handlers;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Handlers;

public class UserAgentDelegatingHandlerTests
{
    [Fact]
    public async Task SendAsync_AddsUserAgentHeader()
    {
        var innerHandler = CreateMockInnerHandler();
        var handler = new UserAgentDelegatingHandler("TestApp", "1.0.0")
        {
            InnerHandler = innerHandler.Object,
        };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("http://localhost/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        innerHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Headers.UserAgent.ToString().Contains("TestApp/1.0.0")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public void Constructor_CustomNameAndVersion_SetsUserAgentValues()
    {
        var handler = new UserAgentDelegatingHandler("My-Agent", "2.5.0");

        handler.UserAgentValues.Should().HaveCount(2);
        handler.UserAgentValues[0].Product.Should().NotBeNull();
        handler.UserAgentValues[0].Product!.Name.Should().Be("My-Agent");
        handler.UserAgentValues[0].Product!.Version.Should().Be("2.5.0");
    }

    [Fact]
    public void Constructor_NameWithSpaces_ReplacesWithHyphens()
    {
        var handler = new UserAgentDelegatingHandler("My Cool App", "1.0.0");

        handler.UserAgentValues[0].Product!.Name.Should().Be("My-Cool-App");
    }

    private static Mock<HttpMessageHandler> CreateMockInnerHandler()
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        return mock;
    }
}
