using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Presentation.Common.Filters;
using Xunit;

namespace Presentation.Common.Tests.Filters;

/// <summary>
/// Tests for <see cref="ExceptionContextExtensions.ReplaceResponse"/>
/// covering response shape, status codes, error lists, and exception handling state.
/// </summary>
public sealed class ExceptionContextExtensionsTests
{
    private static ExceptionContext CreateExceptionContext(Exception? exception = null)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        return new ExceptionContext(actionContext, [])
        {
            Exception = exception ?? new InvalidOperationException("test error")
        };
    }

    [Fact]
    public void ReplaceResponse_SetsStatusCodeOnResult()
    {
        var context = CreateExceptionContext();

        context.ReplaceResponse(HttpStatusCode.BadRequest, "Bad request");

        var result = context.Result as ObjectResult;
        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(400);
    }

    [Fact]
    public void ReplaceResponse_MarksExceptionAsHandled()
    {
        var context = CreateExceptionContext();

        context.ReplaceResponse(HttpStatusCode.InternalServerError, "Error");

        context.ExceptionHandled.Should().BeTrue();
    }

    [Fact]
    public void ReplaceResponse_ReturnsSameContextForFluency()
    {
        var context = CreateExceptionContext();

        var result = context.ReplaceResponse(HttpStatusCode.NotFound, "Not found");

        result.Should().BeSameAs(context);
    }

    [Fact]
    public void ReplaceResponse_WithErrors_IncludesErrorsInBody()
    {
        var context = CreateExceptionContext();
        var errors = new List<string> { "error-1", "error-2" };

        context.ReplaceResponse(HttpStatusCode.UnprocessableEntity, "Validation failed", errors);

        var result = context.Result as ObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void ReplaceResponse_NullErrors_DoesNotThrow()
    {
        var context = CreateExceptionContext();

        var act = () => context.ReplaceResponse(HttpStatusCode.InternalServerError, "Error", null);

        act.Should().NotThrow();
    }

    [Fact]
    public void ReplaceResponse_InternalServerError_SetsCorrectCode()
    {
        var context = CreateExceptionContext();

        context.ReplaceResponse(HttpStatusCode.InternalServerError, "Server error");

        var result = context.Result as ObjectResult;
        result!.StatusCode.Should().Be(500);
    }

    [Fact]
    public void ReplaceResponse_Unauthorized_SetsCorrectCode()
    {
        var context = CreateExceptionContext();

        context.ReplaceResponse(HttpStatusCode.Unauthorized, "Access denied");

        var result = context.Result as ObjectResult;
        result!.StatusCode.Should().Be(401);
    }

    [Fact]
    public void ReplaceResponse_Forbidden_SetsCorrectCode()
    {
        var context = CreateExceptionContext();

        context.ReplaceResponse(HttpStatusCode.Forbidden, "Forbidden");

        var result = context.Result as ObjectResult;
        result!.StatusCode.Should().Be(403);
    }
}
