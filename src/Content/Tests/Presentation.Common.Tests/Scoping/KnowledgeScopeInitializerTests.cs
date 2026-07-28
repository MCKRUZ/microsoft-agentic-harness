using System.Security.Claims;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using FluentAssertions;
using Moq;
using Presentation.Common.Scoping;
using Xunit;

namespace Presentation.Common.Tests.Scoping;

/// <summary>
/// Tests for <see cref="KnowledgeScopeInitializer"/> — the shared user/tenant → scope mapping used
/// by both the HTTP middleware and the SignalR hub filter. This is the security-critical chokepoint
/// that attributes memory and graph operations to the authenticated identity, and that decides whether
/// a caller whose identity cannot be resolved may proceed at all.
/// </summary>
public sealed class KnowledgeScopeInitializerTests
{
    private readonly Mock<IKnowledgeScopeWriter> _writer = new();

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));

    [Fact]
    public void TryApply_SetsUserAndTenant_FromClaims()
    {
        var user = Authenticated(("oid", "user-1"), ("tid", "tenant-1"));

        KnowledgeScopeInitializer.TryApply(user, _writer.Object, out _).Should().BeTrue();

        _writer.Verify(w => w.SetScope("user-1", "tenant-1", null, null, null), Times.Once);
    }

    [Fact]
    public void TryApply_SetsUser_WithNullTenant_WhenNoTidClaim()
    {
        var user = Authenticated(("oid", "user-1"));

        KnowledgeScopeInitializer.TryApply(user, _writer.Object, out _).Should().BeTrue();

        _writer.Verify(w => w.SetScope("user-1", null, null, null, null), Times.Once);
    }

    [Fact]
    public void TryApply_AllowsUnauthenticatedCaller_ToProceedUnscoped()
    {
        // No credentials presented means the caller has not asked to own anything. Anonymous endpoints
        // and health probes depend on this path; authorization guards the rest.
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "user-1")]));

        KnowledgeScopeInitializer.TryApply(user, _writer.Object, out _).Should().BeTrue();

        _writer.VerifyNoOtherCalls();
    }

    [Fact]
    public void TryApply_AllowsNullPrincipal_ToProceedUnscoped()
    {
        KnowledgeScopeInitializer.TryApply(null, _writer.Object, out _).Should().BeTrue();

        _writer.VerifyNoOtherCalls();
    }

    // --- Fail-closed: authenticated but unresolvable ---

    [Fact]
    public void TryApply_RejectsAuthenticatedCaller_WithAmbiguousIdentity()
    {
        // THE DEFECT THIS CLOSES. Two conflicting oid values resolve to null. Previously null meant
        // "leave the scope unset and carry on" — and an unset owner is GLOBAL, so the caller silently
        // wrote world-readable records. An identity we cannot determine must stop the request.
        var user = Authenticated(("oid", "victim"), ("oid", "attacker"));

        KnowledgeScopeInitializer.TryApply(user, _writer.Object, out _).Should().BeFalse(
            "an ambiguous identity must reject the request, not degrade to an unscoped (global) write");

        _writer.VerifyNoOtherCalls();
    }

    [Fact]
    public void TryApply_RejectsAuthenticatedCaller_WithNoStableIdentityClaim()
    {
        var user = Authenticated((ClaimTypes.Name, "Display Name"), ("upn", "alice@contoso.com"));

        KnowledgeScopeInitializer.TryApply(user, _writer.Object, out _).Should().BeFalse();

        _writer.VerifyNoOtherCalls();
    }

    [Fact]
    public void TryApply_ReturnsInertToken_WhenRejecting()
    {
        // Callers dispose the out token on every path, so it must be safe even on rejection.
        var user = Authenticated(("oid", "victim"), ("oid", "attacker"));

        KnowledgeScopeInitializer.TryApply(user, _writer.Object, out var token);

        token.Should().NotBeNull();
        var dispose = () => token.Dispose();
        dispose.Should().NotThrow();
    }
}
