using System.Security.Claims;
using FluentAssertions;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Tests for <see cref="ClaimsPrincipalExtensions"/> — Azure AD claim extraction used to populate
/// the knowledge scope (user/tenant) at the entry points.
/// </summary>
public sealed class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));

    [Fact]
    public void GetTenantId_ReadsTidClaim()
    {
        var principal = Authenticated(("tid", "tenant-123"));

        principal.GetTenantId().Should().Be("tenant-123");
    }

    [Fact]
    public void GetTenantId_ReadsNamespacedClaim()
    {
        var principal = Authenticated(
            ("http://schemas.microsoft.com/identity/claims/tenantid", "tenant-ns"));

        principal.GetTenantId().Should().Be("tenant-ns");
    }

    [Fact]
    public void GetTenantId_ReturnsNull_WhenAbsent()
    {
        var principal = Authenticated(("oid", "user-1"));

        principal.GetTenantId().Should().BeNull();
    }

    [Fact]
    public void GetUserIdOrNull_ReturnsOid_WhenAuthenticated()
    {
        var principal = Authenticated(("oid", "user-1"));

        principal.GetUserIdOrNull().Should().Be("user-1");
    }

    [Fact]
    public void GetUserIdOrNull_ReturnsNull_WhenUnauthenticated()
    {
        // Identity with no authentication type is not authenticated, even with an oid claim.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "user-1")]));

        principal.GetUserIdOrNull().Should().BeNull();
    }

    [Fact]
    public void GetUserIdOrNull_ReturnsNull_WhenNoStableClaim()
    {
        var principal = Authenticated(("tid", "tenant-1"));

        principal.GetUserIdOrNull().Should().BeNull();
    }

    // --- Stable identity ladder: oid then sub, each in raw or JWT-mapped form ---

    [Fact]
    public void GetUserIdOrNull_ReturnsSub_WhenNoOid()
    {
        // The defect this closes: oid is an Entra-ism, and plenty of OIDC providers issue only sub. A
        // sub-only token that resolved to null got a NULL knowledge scope, and a null owner is GLOBAL —
        // so that caller's plans were readable by everyone.
        var principal = Authenticated(("sub", "subject-1"));

        principal.GetUserIdOrNull().Should().Be("subject-1");
    }

    [Fact]
    public void GetUserIdOrNull_ReturnsMappedSub_WhenTokenWasInboundMapped()
    {
        // JWT inbound mapping rewrites "sub" to the NameIdentifier URI on validated production tokens.
        // A raw FindFirst("sub") finds nothing there — the claim-mapping trap.
        var principal = Authenticated((ClaimTypes.NameIdentifier, "subject-mapped"));

        principal.GetUserIdOrNull().Should().Be("subject-mapped");
    }

    [Fact]
    public void GetUserIdOrNull_ReturnsMappedOid_WhenTokenWasInboundMapped()
    {
        var principal = Authenticated(
            ("http://schemas.microsoft.com/identity/claims/objectidentifier", "oid-mapped"));

        principal.GetUserIdOrNull().Should().Be("oid-mapped");
    }

    [Fact]
    public void GetUserIdOrNull_PrefersOid_OverSub()
    {
        var principal = Authenticated(("oid", "the-oid"), ("sub", "the-sub"));

        principal.GetUserIdOrNull().Should().Be("the-oid");
    }

    [Fact]
    public void GetUserIdOrNull_TreatsRawAndMappedFormsOfOneValueAsSingleIdentity()
    {
        // Same value under both forms is one identity, not an ambiguity.
        var principal = Authenticated(
            ("oid", "same-value"),
            ("http://schemas.microsoft.com/identity/claims/objectidentifier", "same-value"));

        principal.GetUserIdOrNull().Should().Be("same-value");
    }

    [Fact]
    public void GetUserIdOrNull_ReturnsNull_WhenOidIsAmbiguous()
    {
        // An attacker who can smuggle a second claim instance must not get to pick which one wins.
        var principal = Authenticated(("oid", "victim"), ("oid", "attacker"));

        principal.GetUserIdOrNull().Should().BeNull();
    }

    [Fact]
    public void GetUserIdOrNull_AmbiguousOid_DoesNotFallThroughToSub()
    {
        // Falling through would let a poisoned oid FORCE selection of an attacker-controlled sub.
        var principal = Authenticated(("oid", "victim"), ("oid", "attacker"), ("sub", "attacker-sub"));

        principal.GetUserIdOrNull().Should().BeNull();
    }

    [Fact]
    public void GetUserIdOrNull_ReturnsNull_WhenTwoValuesDifferOnlyByCase()
    {
        // Ownership is compared ordinally downstream (record.UserId != callerId), so "VICTIM" and
        // "victim" are two different owners there. Deduping them case-insensitively here would
        // resolve one identity and hand the record to whichever casing happened to arrive first —
        // an attacker-chosen one, if they can smuggle a claim. Ambiguous, therefore rejected.
        var principal = Authenticated(("oid", "victim"), ("oid", "VICTIM"));

        principal.GetUserIdOrNull().Should().BeNull();
    }

    [Fact]
    public void GetUserIdOrNull_IgnoresMutableAndDisplayNameClaims()
    {
        // upn/preferred_username are reassignable and name is not unique — none may key ownership.
        var principal = Authenticated(
            ("upn", "alice@contoso.com"),
            ("preferred_username", "alice@contoso.com"),
            (ClaimTypes.Name, "Alice Smith"));

        principal.GetUserIdOrNull().Should().BeNull();
    }

    [Fact]
    public void GetUserId_ThrowsWhenNoStableClaim()
    {
        var principal = Authenticated((ClaimTypes.Name, "Alice Smith"));

        var act = () => principal.GetUserId();

        act.Should().Throw<InvalidOperationException>().WithMessage("*stable identity*");
    }

    [Fact]
    public void GetUserId_ResolvesSub_MatchingTheNullableVariant()
    {
        // The throwing and non-throwing variants must walk the SAME ladder; a divergence here is how
        // the two-resolver defect started.
        var principal = Authenticated(("sub", "subject-1"));

        principal.GetUserId().Should().Be("subject-1");
    }
}
