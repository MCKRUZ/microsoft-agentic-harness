using System.Text;
using Application.Common.Exceptions;
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

    [Fact]
    public async Task InvokeAsync_ExceptionDerivedFromAMappedType_UsesTheNearestMappedAncestor()
    {
        // Every exception type this project owns is sealed, so UnauthorizedAccessException — supplied
        // by the framework — is the only mapped type anything can derive from, and therefore the only
        // place the old exact-type lookup could go wrong. It did: a derived refusal matched nothing
        // and was reported as an unhandled fault, which says the server is broken when in fact the
        // request was understood and declined.
        _envMock.Setup(e => e.EnvironmentName).Returns(Environments.Development);
        var middleware = CreateMiddleware(_ =>
            throw new DerivedUnauthorizedException("Token has no tenant claim"));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_DerivedExceptionWithItsOwnEntry_KeepsItsOwnStatusNotTheBaseTypes()
    {
        // Precedence guard. ConversationAccessDeniedException derives from UnauthorizedAccessException
        // (401) but is deliberately 403: the caller IS authenticated, and telling them to authenticate
        // again sends them round a loop that cannot succeed. Walking the chain must therefore stop at
        // the first match, not the last — an exact entry always beats an inherited one.
        var middleware = CreateMiddleware(_ =>
            throw new ConversationAccessDeniedException());

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionDeclaringItsOwnStatus_BeatsWhatItWouldInherit()
    {
        // The reason IHttpStatusException exists. This exception derives from
        // UnauthorizedAccessException and would inherit 401, but it states 403. A type that answers
        // for itself is never guessing, so its answer must win — otherwise an exception declared in a
        // layer above this middleware can only ever get a status by inheriting a wrong one.
        _envMock.Setup(e => e.EnvironmentName).Returns(Environments.Development);
        var middleware = CreateMiddleware(_ =>
            throw new SelfDeclaringForbiddenException("Path outside the sandbox"));

        await middleware.InvokeAsync(_httpContext);

        _httpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionDeclaringItsOwnStatus_InProduction_ReturnsOnlyItsSafeMessage()
    {
        // The thrown message names what was refused. Outside development the caller must get the
        // exception's SafeMessage instead — an error body is as readable as a successful one.
        _envMock.Setup(e => e.EnvironmentName).Returns(Environments.Production);
        var body = new MemoryStream();
        _httpContext.Response.Body = body;
        var middleware = CreateMiddleware(_ =>
            throw new SelfDeclaringForbiddenException("C:/secrets/master.key is outside the sandbox"));

        await middleware.InvokeAsync(_httpContext);

        var written = Encoding.UTF8.GetString(body.ToArray());
        written.Should().NotContain("master.key");
        written.Should().Contain("Forbidden.");
    }

    private sealed class DerivedUnauthorizedException(string message) : UnauthorizedAccessException(message);

    private sealed class SelfDeclaringForbiddenException(string message)
        : UnauthorizedAccessException(message), IHttpStatusException
    {
        public int StatusCode => StatusCodes.Status403Forbidden;

        public string SafeMessage => "Forbidden.";
    }
}
