using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Infrastructure.APIAccess.Handlers;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Handlers;

/// <summary>
/// Extended tests for <see cref="UserAgentDelegatingHandler"/> covering
/// null guards, custom header values, and OS version inclusion.
/// </summary>
public sealed class UserAgentDelegatingHandlerExtendedTests
{
    [Fact]
    public void Constructor_NullApplicationName_ThrowsArgumentNullException()
    {
        var act = () => new UserAgentDelegatingHandler(null!, "1.0.0");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullApplicationVersion_ThrowsArgumentNullException()
    {
        var act = () => new UserAgentDelegatingHandler("App", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullUserAgentValues_ThrowsArgumentNullException()
    {
        var act = () => new UserAgentDelegatingHandler((IReadOnlyList<ProductInfoHeaderValue>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_CustomValues_StoresValuesCorrectly()
    {
        var values = new List<ProductInfoHeaderValue>
        {
            new("TestProduct", "1.0.0"),
            new("(TestComment)")
        };

        var handler = new UserAgentDelegatingHandler(values);

        handler.UserAgentValues.Should().HaveCount(2);
    }

    [Fact]
    public void Constructor_NameAndVersion_IncludesOsVersionComment()
    {
        var handler = new UserAgentDelegatingHandler("TestApp", "1.0.0");

        handler.UserAgentValues.Should().HaveCount(2);
        handler.UserAgentValues[1].Comment.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SendAsync_MultipleRequests_AddsHeaderToEach()
    {
        var innerHandler = CreateMockInnerHandler();
        var handler = new UserAgentDelegatingHandler("MultiTest", "2.0.0")
        {
            InnerHandler = innerHandler.Object
        };
        using var client = new HttpClient(handler);

        await client.GetAsync("http://localhost/first");
        await client.GetAsync("http://localhost/second");

        innerHandler.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Headers.UserAgent.ToString().Contains("MultiTest/2.0.0")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public void Constructor_NullAssembly_ThrowsArgumentNullException()
    {
        var act = () => new UserAgentDelegatingHandler(
            (System.Reflection.Assembly)null!);

        act.Should().Throw<ArgumentNullException>();
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
