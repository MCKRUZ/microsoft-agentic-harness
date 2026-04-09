using Domain.Common.Config.Http;
using FluentAssertions;
using Infrastructure.Common.Middleware.EndpointFilters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Infrastructure.Common.Tests.Middleware.EndpointFilters;

public class HttpAuthEndpointFilterTests
{
    private const string ValidKey1 = "primary-test-key-12345";
    private const string ValidKey2 = "secondary-test-key-67890";
    private const string HeaderName = "X-API-Key";

    private static HttpAuthorizationConfig CreateConfig(bool enabled = true) => new()
    {
        Enabled = enabled,
        HttpHeaderName = HeaderName,
        AccessKey1 = ValidKey1,
        AccessKey2 = ValidKey2,
    };

    private static (DefaultHttpContext HttpContext, EndpointFilterInvocationContext FilterContext) CreateContexts()
    {
        var httpContext = new DefaultHttpContext();
        var filterContext = new DefaultEndpointFilterInvocationContext(httpContext);
        return (httpContext, filterContext);
    }

    private static EndpointFilterDelegate CreateNextDelegate(object? returnValue = null)
    {
        return _ => new ValueTask<object?>(returnValue ?? "ok");
    }

    [Fact]
    public async Task InvokeAsync_MissingApiKey_Returns401()
    {
        var filter = new HttpAuthEndpointFilter(CreateConfig());
        var (_, filterContext) = CreateContexts();

        var result = await filter.InvokeAsync(filterContext, CreateNextDelegate());

        result.Should().BeAssignableTo<ProblemHttpResult>();
        var problem = (ProblemHttpResult)result!;
        problem.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_InvalidApiKey_Returns403()
    {
        var filter = new HttpAuthEndpointFilter(CreateConfig());
        var (httpContext, filterContext) = CreateContexts();
        httpContext.Request.Headers[HeaderName] = "wrong-key";

        var result = await filter.InvokeAsync(filterContext, CreateNextDelegate());

        result.Should().BeAssignableTo<ProblemHttpResult>();
        var problem = (ProblemHttpResult)result!;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task InvokeAsync_ValidAccessKey1_PassesThrough()
    {
        var filter = new HttpAuthEndpointFilter(CreateConfig());
        var (httpContext, filterContext) = CreateContexts();
        httpContext.Request.Headers[HeaderName] = ValidKey1;

        var result = await filter.InvokeAsync(filterContext, CreateNextDelegate("success"));

        result.Should().Be("success");
    }

    [Fact]
    public async Task InvokeAsync_ValidAccessKey2_PassesThrough()
    {
        var filter = new HttpAuthEndpointFilter(CreateConfig());
        var (httpContext, filterContext) = CreateContexts();
        httpContext.Request.Headers[HeaderName] = ValidKey2;

        var result = await filter.InvokeAsync(filterContext, CreateNextDelegate("success"));

        result.Should().Be("success");
    }

    [Fact]
    public async Task InvokeAsync_AuthDisabled_PassesThroughWithoutKey()
    {
        var filter = new HttpAuthEndpointFilter(CreateConfig(enabled: false));
        var (_, filterContext) = CreateContexts();

        var result = await filter.InvokeAsync(filterContext, CreateNextDelegate("bypassed"));

        result.Should().Be("bypassed");
    }
}
