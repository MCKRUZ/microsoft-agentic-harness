using System.Security.Claims;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using Presentation.Common.Scoping;
using Xunit;

namespace Presentation.Common.Tests.Scoping;

/// <summary>
/// Tests for <see cref="KnowledgeScopeMiddleware"/> — establishes per-request scope from the
/// authenticated principal, lets unauthenticated callers through unscoped, and REJECTS an authenticated
/// caller whose identity cannot be resolved.
/// </summary>
public sealed class KnowledgeScopeMiddlewareTests
{
    private readonly Mock<IKnowledgeScopeWriter> _writer = new();

    private static DefaultHttpContext ContextFor(params Claim[] claims) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
    };

    [Fact]
    public async Task InvokeAsync_SetsScope_ForAuthenticatedUser_AndCallsNext()
    {
        var nextCalled = false;
        var middleware = new KnowledgeScopeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("oid", "user-1"), new Claim("tid", "tenant-1")], "TestAuth"))
        };

        await middleware.InvokeAsync(context, _writer.Object);

        _writer.Verify(w => w.SetScope("user-1", "tenant-1", null, null, null), Times.Once);
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_DoesNotSetScope_ForAnonymous_ButStillCallsNext()
    {
        var nextCalled = false;
        var middleware = new KnowledgeScopeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext(); // unauthenticated User

        await middleware.InvokeAsync(context, _writer.Object);

        _writer.VerifyNoOtherCalls();
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_Returns401_AndDoesNotCallNext_ForAmbiguousIdentity()
    {
        // The request must DIE here. Reaching next() would run authenticated work with no owner, and an
        // unowned record is world-readable — the third route by which "identity resolved to nothing"
        // became a global write.
        var nextCalled = false;
        var middleware = new KnowledgeScopeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = ContextFor(new Claim("oid", "victim"), new Claim("oid", "attacker"));

        await middleware.InvokeAsync(context, _writer.Object);

        nextCalled.Should().BeFalse("an unresolvable identity must never reach the rest of the pipeline");
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeAsync_Returns401_ForAuthenticatedCallerWithNoStableClaim()
    {
        var nextCalled = false;
        var middleware = new KnowledgeScopeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = ContextFor(new Claim(ClaimTypes.Name, "Display Name"));

        await middleware.InvokeAsync(context, _writer.Object);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_RejectionBody_DoesNotRevealWhichClaimFailed()
    {
        // Telling a caller probing with injected claims which one was missing or ambiguous hands them
        // the next thing to try.
        var middleware = new KnowledgeScopeMiddleware(_ => Task.CompletedTask);
        var context = ContextFor(new Claim("oid", "victim"), new Claim("oid", "attacker"));
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, _writer.Object);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().NotContainAny("oid", "sub", "victim", "attacker", "ambiguous");
    }
}
