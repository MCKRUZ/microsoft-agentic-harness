using System.Net;
using FluentAssertions;
using Infrastructure.APIAccess.Handlers;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Handlers;

public class LoggingDelegatingHandlerTests
{
    [Fact]
    public async Task SendAsync_LogsRequestAtDebugLevel()
    {
        var logger = new Mock<ILogger<LoggingDelegatingHandler>>();
        var innerHandler = CreateMockInnerHandler();
        var handler = new LoggingDelegatingHandler(logger.Object)
        {
            InnerHandler = innerHandler.Object,
        };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("http://localhost/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        logger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Outbound HTTP request")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_ForwardsRequestToInnerHandler()
    {
        var logger = new Mock<ILogger<LoggingDelegatingHandler>>();
        var innerHandler = CreateMockInnerHandler();
        var handler = new LoggingDelegatingHandler(logger.Object)
        {
            InnerHandler = innerHandler.Object,
        };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("http://localhost/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        innerHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
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
