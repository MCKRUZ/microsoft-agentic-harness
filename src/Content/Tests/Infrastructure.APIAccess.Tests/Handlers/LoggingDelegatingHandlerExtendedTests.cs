using System.Net;
using FluentAssertions;
using Infrastructure.APIAccess.Handlers;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Handlers;

/// <summary>
/// Extended tests for <see cref="LoggingDelegatingHandler"/> covering
/// null guards and various HTTP methods.
/// </summary>
public sealed class LoggingDelegatingHandlerExtendedTests
{
    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new LoggingDelegatingHandler(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task SendAsync_PostRequest_LogsDebug()
    {
        var logger = new Mock<ILogger<LoggingDelegatingHandler>>();
        var innerHandler = CreateMockInnerHandler();
        var handler = new LoggingDelegatingHandler(logger.Object)
        {
            InnerHandler = innerHandler.Object
        };
        using var client = new HttpClient(handler);

        await client.PostAsync("http://localhost/test", new StringContent("body"));

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
    public async Task SendAsync_DeleteRequest_LogsDebug()
    {
        var logger = new Mock<ILogger<LoggingDelegatingHandler>>();
        var innerHandler = CreateMockInnerHandler();
        var handler = new LoggingDelegatingHandler(logger.Object)
        {
            InnerHandler = innerHandler.Object
        };
        using var client = new HttpClient(handler);

        await client.DeleteAsync("http://localhost/test");

        logger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Outbound HTTP request")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
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
