using FluentAssertions;
using Infrastructure.Common.Middleware.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Infrastructure.Common.Tests.Middleware.Security;

public class SecurityHeadersMiddlewareTests
{
    private readonly DefaultHttpContext _httpContext = new();
    private bool _nextCalled;

    private SecurityHeadersMiddleware CreateMiddleware()
    {
        return new SecurityHeadersMiddleware(_ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task InvokeAsync_SetsXContentTypeOptions_ToNosniff()
    {
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.Headers["X-Content-Type-Options"]
            .ToString().Should().Be("nosniff");
    }

    [Fact]
    public async Task InvokeAsync_SetsXFrameOptions_ToDeny()
    {
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.Headers["X-Frame-Options"]
            .ToString().Should().Be("DENY");
    }

    [Fact]
    public async Task InvokeAsync_SetsXXssProtection()
    {
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.Headers["X-XSS-Protection"]
            .ToString().Should().Be("1; mode=block");
    }

    [Fact]
    public async Task InvokeAsync_SetsContentSecurityPolicy()
    {
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.Headers["Content-Security-Policy"]
            .ToString().Should().Contain("default-src 'self'");
    }

    [Fact]
    public async Task InvokeAsync_SetsStrictTransportSecurity()
    {
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(_httpContext);

        var hsts = _httpContext.Response.Headers["Strict-Transport-Security"].ToString();
        hsts.Should().Contain("max-age=31536000");
        hsts.Should().Contain("includeSubDomains");
    }

    [Fact]
    public async Task InvokeAsync_SetsReferrerPolicy_ToNoReferrer()
    {
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.Headers["Referrer-Policy"]
            .ToString().Should().Be("no-referrer");
    }

    [Fact]
    public async Task InvokeAsync_SetsPermissionsPolicy()
    {
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(_httpContext);

        var policy = _httpContext.Response.Headers["Permissions-Policy"].ToString();
        policy.Should().Contain("geolocation=()");
        policy.Should().Contain("microphone=()");
        policy.Should().Contain("camera=()");
    }

    [Fact]
    public async Task InvokeAsync_CallsNextMiddleware()
    {
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(_httpContext);

        _nextCalled.Should().BeTrue();
    }
}
