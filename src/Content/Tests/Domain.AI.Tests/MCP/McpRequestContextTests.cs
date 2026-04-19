using System.Security.Claims;
using Domain.AI.MCP;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.MCP;

/// <summary>
/// Tests for <see cref="McpRequestContext"/> — authentication checks, factory methods, null handling.
/// </summary>
public sealed class McpRequestContextTests
{
    [Fact]
    public void Unauthenticated_Principal_IsNull()
    {
        var ctx = McpRequestContext.Unauthenticated;

        ctx.Principal.Should().BeNull();
    }

    [Fact]
    public void Unauthenticated_IsAuthenticated_ReturnsFalse()
    {
        var ctx = McpRequestContext.Unauthenticated;

        ctx.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public void Unauthenticated_IsSingleton()
    {
        var ctx1 = McpRequestContext.Unauthenticated;
        var ctx2 = McpRequestContext.Unauthenticated;

        ctx1.Should().BeSameAs(ctx2);
    }

    [Fact]
    public void FromPrincipal_WithAuthenticatedPrincipal_ReturnsAuthenticatedContext()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "testuser")],
            authenticationType: "Bearer");
        var principal = new ClaimsPrincipal(identity);

        var ctx = McpRequestContext.FromPrincipal(principal);

        ctx.IsAuthenticated.Should().BeTrue();
        ctx.Principal.Should().BeSameAs(principal);
    }

    [Fact]
    public void FromPrincipal_WithUnauthenticatedPrincipal_ReturnsFalse()
    {
        var identity = new ClaimsIdentity(); // no auth type = unauthenticated
        var principal = new ClaimsPrincipal(identity);

        var ctx = McpRequestContext.FromPrincipal(principal);

        ctx.IsAuthenticated.Should().BeFalse();
        ctx.Principal.Should().NotBeNull();
    }

    [Fact]
    public void FromPrincipal_NullPrincipal_ThrowsArgumentNullException()
    {
        var act = () => McpRequestContext.FromPrincipal(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("principal");
    }
}
