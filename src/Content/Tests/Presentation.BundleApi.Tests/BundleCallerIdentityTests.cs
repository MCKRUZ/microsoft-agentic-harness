using System.Security.Claims;
using FluentAssertions;
using Presentation.BundleApi.Services;
using Xunit;

namespace Presentation.BundleApi.Tests;

/// <summary>
/// Tests for <see cref="BundleCallerIdentity.StableId"/> — the per-principal-unique id that owner-binding and
/// rate-limit partitioning depend on. It must prefer the Entra object id, never fall back to the non-unique
/// display name, and return null (not a shared constant) when no stable claim is present.
/// </summary>
public sealed class BundleCallerIdentityTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void StableId_PrefersOid()
    {
        var user = Principal(("oid", "the-oid"), ("sub", "the-sub"), (ClaimTypes.Name, "Display Name"));

        BundleCallerIdentity.StableId(user).Should().Be("the-oid");
    }

    [Fact]
    public void StableId_FallsBackToSubject_WhenNoOid()
    {
        var user = Principal(("sub", "the-sub"), (ClaimTypes.Name, "Display Name"));

        BundleCallerIdentity.StableId(user).Should().Be("the-sub");
    }

    [Fact]
    public void StableId_IgnoresDisplayName_ReturnsNull_WhenNoStableClaim()
    {
        // A display name is NOT a stable per-principal id — it must never be used as an owner/partition key.
        var user = Principal((ClaimTypes.Name, "Display Name"));

        BundleCallerIdentity.StableId(user).Should().BeNull();
    }

    [Fact]
    public void StableId_ReturnsNull_ForClaimlessPrincipal()
    {
        BundleCallerIdentity.StableId(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull();
    }
}
