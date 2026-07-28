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
    public void GetUserIdOrNull_ReturnsNull_WhenNoOid()
    {
        var principal = Authenticated(("tid", "tenant-1"));

        principal.GetUserIdOrNull().Should().BeNull();
    }
}
