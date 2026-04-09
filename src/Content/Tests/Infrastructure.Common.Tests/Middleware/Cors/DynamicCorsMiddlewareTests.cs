using Domain.Common.Config;
using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.Common.Middleware.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.Common.Tests.Middleware.Cors;

public class DynamicCorsMiddlewareTests
{
    private readonly DefaultHttpContext _httpContext = new();
    private readonly Mock<IOptionsMonitor<AppConfig>> _optionsMock = new();
    private readonly Mock<ILogger<DynamicCorsMiddleware>> _loggerMock = new();
    private bool _nextCalled;

    private DynamicCorsMiddleware CreateMiddleware(string corsOrigins = "")
    {
        var appConfig = new AppConfig
        {
            Http = new HttpConfig { CorsAllowedOrigins = corsOrigins }
        };
        _optionsMock.Setup(o => o.CurrentValue).Returns(appConfig);

        return new DynamicCorsMiddleware(
            _ =>
            {
                _nextCalled = true;
                return Task.CompletedTask;
            },
            _optionsMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task InvokeAsync_AllowedOrigin_SetsCorsHeaders()
    {
        var middleware = CreateMiddleware("https://app.example.com;https://localhost:4200");
        _httpContext.Request.Headers.Origin = "https://app.example.com";

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.Headers["Access-Control-Allow-Origin"]
            .ToString().Should().Be("https://app.example.com");
        _httpContext.Response.Headers["Access-Control-Allow-Methods"]
            .ToString().Should().Contain("GET");
    }

    [Fact]
    public async Task InvokeAsync_DisallowedOrigin_DoesNotSetCorsHeaders()
    {
        var middleware = CreateMiddleware("https://app.example.com");
        _httpContext.Request.Headers.Origin = "https://evil.com";

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.Headers.ContainsKey("Access-Control-Allow-Origin")
            .Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_MissingOrigin_DoesNotSetCorsHeaders()
    {
        var middleware = CreateMiddleware("https://app.example.com");

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.Headers.ContainsKey("Access-Control-Allow-Origin")
            .Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_OptionsPreflight_Returns204()
    {
        var middleware = CreateMiddleware("https://app.example.com");
        _httpContext.Request.Method = HttpMethods.Options;
        _httpContext.Request.Headers.Origin = "https://app.example.com";

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        _nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_NonOptionsRequest_CallsNextMiddleware()
    {
        var middleware = CreateMiddleware("https://app.example.com");
        _httpContext.Request.Method = HttpMethods.Get;
        _httpContext.Request.Headers.Origin = "https://app.example.com";

        await middleware.InvokeAsync(_httpContext);

        _nextCalled.Should().BeTrue();
    }
}
