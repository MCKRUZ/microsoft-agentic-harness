using System.Security.Claims;
using FluentAssertions;
using Presentation.ExecutionApi.Services;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

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

    [Fact]
    public void StableId_ResolvesMappedSubject_FromAProductionShapedToken()
    {
        // JWT inbound mapping rewrites "sub" to the NameIdentifier URI on validated tokens. Ownership must
        // recognise that form, not just the raw claim dev handlers mint.
        var user = Principal((ClaimTypes.NameIdentifier, "mapped-subject"));

        BundleCallerIdentity.StableId(user).Should().Be("mapped-subject");
    }

    [Theory]
    [InlineData("oid", "the-oid")]
    [InlineData("sub", "the-sub")]
    [InlineData(ClaimTypes.NameIdentifier, "the-mapped-sub")]
    [InlineData("http://schemas.microsoft.com/identity/claims/objectidentifier", "the-mapped-oid")]
    public void StableId_AgreesWithTheKnowledgeScopeResolver_ForEveryAcceptedClaimShape(
        string claimType, string value)
    {
        // The anti-drift guard. Bundle ownership and knowledge scope must accept EXACTLY the same token
        // shapes: any shape one accepts and the other rejects yields an owned bundle whose plans carry a
        // null (globally readable) owner. StableId delegates, so this can only fail if someone reintroduces
        // a second precedence ladder here.
        var user = Principal((claimType, value));

        BundleCallerIdentity.StableId(user).Should().Be(user.GetUserIdOrNull());
        BundleCallerIdentity.StableId(user).Should().Be(value);
    }
}
