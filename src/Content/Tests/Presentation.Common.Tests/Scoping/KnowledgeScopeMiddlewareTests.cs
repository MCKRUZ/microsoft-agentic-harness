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
/// authenticated principal and always continues the pipeline.
/// </summary>
public sealed class KnowledgeScopeMiddlewareTests
{
    private readonly Mock<IKnowledgeScopeWriter> _writer = new();

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
}
