using System.Security.Claims;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Moq;
using Presentation.Common.Scoping;
using Xunit;

namespace Presentation.Common.Tests.Scoping;

/// <summary>
/// Tests for <see cref="KnowledgeScopeInitializer"/> — the shared user/tenant → scope mapping used
/// by both the HTTP middleware and the SignalR hub filter. This is the security-critical chokepoint
/// that attributes memory and graph operations to the authenticated identity.
/// </summary>
public sealed class KnowledgeScopeInitializerTests
{
    private readonly Mock<IKnowledgeScopeWriter> _writer = new();

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));

    [Fact]
    public void Apply_SetsUserAndTenant_FromClaims()
    {
        var user = Authenticated(("oid", "user-1"), ("tid", "tenant-1"));

        KnowledgeScopeInitializer.Apply(user, _writer.Object);

        _writer.Verify(w => w.SetScope("user-1", "tenant-1", null, null, null), Times.Once);
    }

    [Fact]
    public void Apply_SetsUser_WithNullTenant_WhenNoTidClaim()
    {
        var user = Authenticated(("oid", "user-1"));

        KnowledgeScopeInitializer.Apply(user, _writer.Object);

        _writer.Verify(w => w.SetScope("user-1", null, null, null, null), Times.Once);
    }

    [Fact]
    public void Apply_DoesNothing_WhenUnauthenticated()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "user-1")]));

        KnowledgeScopeInitializer.Apply(user, _writer.Object);

        _writer.VerifyNoOtherCalls();
    }

    [Fact]
    public void Apply_DoesNothing_WhenPrincipalNull()
    {
        KnowledgeScopeInitializer.Apply(null, _writer.Object);

        _writer.VerifyNoOtherCalls();
    }
}
