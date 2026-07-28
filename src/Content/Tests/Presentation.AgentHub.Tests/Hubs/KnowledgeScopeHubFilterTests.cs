using System.Reflection;
using System.Security.Claims;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.AgentHub.Hubs;
using Xunit;

namespace Presentation.AgentHub.Tests.Hubs;

/// <summary>
/// Tests for <see cref="KnowledgeScopeHubFilter"/> — the SignalR equivalent of the HTTP scope
/// middleware. Hub invocations run on their own DI scope with no HTTP request, so this is the only
/// chokepoint that can attribute a hub method's knowledge writes to the calling user.
/// </summary>
/// <remarks>
/// The rejection case matters as much as the happy path: a hub method that ran unscoped would write
/// records with a null owner, and a null owner reads as GLOBAL — visible to every caller in every
/// tenant. The filter must therefore fail closed exactly as the HTTP middleware does.
/// </remarks>
public sealed class KnowledgeScopeHubFilterTests
{
    private readonly Mock<IKnowledgeScopeWriter> _writer = new();

    [Fact]
    public async Task InvokeMethodAsync_SetsScope_ForAuthenticatedUser_AndInvokesMethod()
    {
        var invoked = false;
        var context = CreateContext(new Claim("oid", "user-1"), new Claim("tid", "tenant-1"));

        await new KnowledgeScopeHubFilter().InvokeMethodAsync(context, _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        _writer.Verify(w => w.SetScope("user-1", "tenant-1", null, null, null), Times.Once);
        invoked.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeMethodAsync_Throws_AndDoesNotInvokeMethod_ForAmbiguousIdentity()
    {
        var invoked = false;
        var context = CreateContext(new Claim("oid", "victim"), new Claim("oid", "attacker"));

        var act = async () => await new KnowledgeScopeHubFilter().InvokeMethodAsync(context, _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        await act.Should().ThrowAsync<HubException>();
        invoked.Should().BeFalse("an unresolvable identity must never reach the hub method");
        _writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeMethodAsync_AllowsUnauthenticatedCaller_ToProceedUnscoped()
    {
        var invoked = false;
        var context = CreateContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "user-1")])));

        await new KnowledgeScopeHubFilter().InvokeMethodAsync(context, _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        invoked.Should().BeTrue();
        _writer.VerifyNoOtherCalls();
    }

    // -- Helpers --

    private HubInvocationContext CreateContext(params Claim[] claims) =>
        CreateContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")));

    private HubInvocationContext CreateContext(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_writer.Object);

        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(c => c.User).Returns(user);

        return new HubInvocationContext(
            callerContext.Object,
            services.BuildServiceProvider(),
            new StubHub(),
            typeof(StubHub).GetMethod(nameof(StubHub.Ping), BindingFlags.Public | BindingFlags.Instance)!,
            []);
    }

    /// <summary>Minimal hub so a <see cref="HubInvocationContext"/> can be constructed.</summary>
    private sealed class StubHub : Hub
    {
        public void Ping()
        {
            // Never invoked — the filter's continuation delegate stands in for the method body.
        }
    }
}
