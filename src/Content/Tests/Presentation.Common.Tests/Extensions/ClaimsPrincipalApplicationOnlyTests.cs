using System.Security.Claims;
using FluentAssertions;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// <see cref="ClaimsPrincipalExtensions.IsApplicationOnly"/> (#907). A false negative lets one application's callers erase
/// each other's data; a false positive refuses a real person their own erasure. Both directions are asserted.
/// </summary>
public sealed class ClaimsPrincipalApplicationOnlyTests
{
    private const string MappedOid = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    public static TheoryData<(string, string)[]> ApplicationTokens => new()
    {
        // Entra's documented check: an app-only token's sub is the service principal, equal to its oid.
        new[] { ("oid", "sp-1"), ("sub", "sp-1") },
        // The same with inbound claim mapping on, as production tokens arrive.
        new[] { (MappedOid, "sp-1"), (ClaimTypes.NameIdentifier, "sp-1") },
        new[] { ("oid", "sp-1"), (ClaimTypes.NameIdentifier, "sp-1") },
        // The optional idtyp claim, when a tenant turns it on, settles it whatever the other claims say.
        new[] { ("oid", "sp-1"), ("sub", "pairwise-1"), ("idtyp", "app") },
        new[] { ("idtyp", "APP") },
        // Ambiguous: a second sub equal to the oid fails closed.
        new[] { ("oid", "sp-1"), ("sub", "pairwise-1"), (ClaimTypes.NameIdentifier, "sp-1") },
    };

    public static TheoryData<(string, string)[]> PersonTokens => new()
    {
        // A person's sub is a pairwise id that differs from their oid.
        new[] { ("oid", "user-1"), ("sub", "pairwise-user-1") },
        new[] { (MappedOid, "user-1"), (ClaimTypes.NameIdentifier, "pairwise-user-1") },
        new[] { ("oid", "user-1"), ("sub", "pairwise-user-1"), ("idtyp", "user") },
        // Providers that issue only one of the two are never read as an application.
        new[] { ("oid", "user-1") },
        new[] { ("sub", "user-1") },
        // No scp is not a signal: other providers omit it for people.
        new[] { ("sub", "user-1"), ("roles", "Reader") },
    };

    [Theory]
    [MemberData(nameof(ApplicationTokens))]
    public void IsApplicationOnly_ApplicationToken_IsTrue((string, string)[] claims)
    {
        Authenticated(claims).IsApplicationOnly().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(PersonTokens))]
    public void IsApplicationOnly_PersonsToken_IsFalse((string, string)[] claims)
    {
        Authenticated(claims).IsApplicationOnly().Should().BeFalse();
    }

    [Fact]
    public void IsApplicationOnly_Unauthenticated_IsFalse()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "sp-1"), new Claim("sub", "sp-1")]));

        anonymous.IsApplicationOnly().Should().BeFalse();
    }
}
