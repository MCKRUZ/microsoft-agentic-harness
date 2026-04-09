using System.Net;
using CorrelationId;
using CorrelationId.Abstractions;
using FluentAssertions;
using Infrastructure.APIAccess.Handlers;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Handlers;

public class CorrelationIdDelegatingHandlerTests
{
    private const string TestCorrelationId = "test-correlation-123";
    private const string TestHeaderName = "X-Correlation-ID";

    [Fact]
    public async Task SendAsync_WithCorrelationContext_AddsHeader()
    {
        var accessor = new Mock<ICorrelationContextAccessor>();
        accessor.Setup(a => a.CorrelationContext)
            .Returns(new CorrelationContext(TestCorrelationId, TestHeaderName));

        var options = Options.Create(new CorrelationIdOptions
        {
            RequestHeader = TestHeaderName,
        });

        var innerHandler = CreateMockInnerHandler();
        var handler = new CorrelationIdDelegatingHandler(accessor.Object, options)
        {
            InnerHandler = innerHandler.Object,
        };
        using var client = new HttpClient(handler);

        await client.GetAsync("http://localhost/test");

        innerHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Headers.Contains(TestHeaderName) &&
                r.Headers.GetValues(TestHeaderName).First() == TestCorrelationId),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithoutCorrelationContext_DoesNotAddHeader()
    {
        // Mock defaults to null for CorrelationContext -- no explicit setup needed
        var accessor = new Mock<ICorrelationContextAccessor>();

        var options = Options.Create(new CorrelationIdOptions
        {
            RequestHeader = TestHeaderName,
        });

        var innerHandler = CreateMockInnerHandler();
        var handler = new CorrelationIdDelegatingHandler(accessor.Object, options)
        {
            InnerHandler = innerHandler.Object,
        };
        using var client = new HttpClient(handler);

        await client.GetAsync("http://localhost/test");

        innerHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                !r.Headers.Contains(TestHeaderName)),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_HeaderAlreadyPresent_DoesNotOverwrite()
    {
        var accessor = new Mock<ICorrelationContextAccessor>();
        accessor.Setup(a => a.CorrelationContext)
            .Returns(new CorrelationContext(TestCorrelationId, TestHeaderName));

        var options = Options.Create(new CorrelationIdOptions
        {
            RequestHeader = TestHeaderName,
        });

        var innerHandler = CreateMockInnerHandler();
        var handler = new CorrelationIdDelegatingHandler(accessor.Object, options)
        {
            InnerHandler = innerHandler.Object,
        };
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
        request.Headers.Add(TestHeaderName, "existing-id");

        await client.SendAsync(request);

        innerHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Headers.GetValues(TestHeaderName).Single() == "existing-id"),
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
