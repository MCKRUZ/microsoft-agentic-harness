using Application.Common.Exceptions.ExceptionTypes;
using FluentAssertions;
using Infrastructure.Common.Middleware.ExceptionHandling;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.Common.Tests.Middleware.ExceptionHandling;

public class GlobalExceptionMiddlewareTests
{
    private readonly DefaultHttpContext _httpContext = new();
    private readonly Mock<IWebHostEnvironment> _envMock = new();
    private readonly Mock<ILogger<GlobalExceptionMiddleware>> _loggerMock = new();

    private GlobalExceptionMiddleware CreateMiddleware(Func<HttpContext, Task>? next = null)
    {
        next ??= _ => Task.CompletedTask;
        return new GlobalExceptionMiddleware(
            new RequestDelegate(next),
            _envMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task InvokeAsync_NoException_PassesThrough()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(_httpContext);

        nextCalled.Should().BeTrue();
        _httpContext.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_BadRequestException_Returns400()
    {
        var middleware = CreateMiddleware(_ =>
            throw new BadRequestException("Invalid input"));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task InvokeAsync_UnauthorizedAccessException_Returns401()
    {
        var middleware = CreateMiddleware(_ =>
            throw new UnauthorizedAccessException("Not authenticated"));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_ForbiddenAccessException_Returns403()
    {
        var middleware = CreateMiddleware(_ =>
            throw new ForbiddenAccessException("Access denied"));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task InvokeAsync_EntityNotFoundException_Returns404()
    {
        var middleware = CreateMiddleware(_ =>
            throw new EntityNotFoundException("User", 42));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task InvokeAsync_DatabaseInteractionException_Returns422()
    {
        var middleware = CreateMiddleware(_ =>
            throw new DatabaseInteractionException("Create", "User"));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task InvokeAsync_NoContentException_Returns204()
    {
        var middleware = CreateMiddleware(_ =>
            throw new NoContentException("No data"));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_InDevelopment_Returns500()
    {
        _envMock.Setup(e => e.EnvironmentName).Returns(Environments.Development);
        var middleware = CreateMiddleware(_ =>
            throw new InvalidOperationException("Something broke"));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_InProduction_Returns400WithGenericMessage()
    {
        _envMock.Setup(e => e.EnvironmentName).Returns(Environments.Production);
        var middleware = CreateMiddleware(_ =>
            throw new InvalidOperationException("Internal details"));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
