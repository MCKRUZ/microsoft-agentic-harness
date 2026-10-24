using System.Security.Claims;
using FluentAssertions;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// <see cref="ClaimsPrincipalExtensions.IsApplicationOnly"/> (#907). A false negative lets one application's callers erase
/// each other's data; a false positive refuses a real person their own erasure. Both directions are asserted, per issuer.
/// </summary>
public sealed class ClaimsPrincipalApplicationOnlyTests
{
    private const string MappedOid = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string MappedScope = "http://schemas.microsoft.com/identity/claims/scope";
    private const string EntraV2 = "https://login.microsoftonline.com/tenant-1/v2.0";
    private const string EntraV1 = "https://sts.windows.net/tenant-1/";
    private const string ExternalId = "https://tenant-1.ciamlogin.com/tenant-1/v2.0";
    private const string B2C = "https://contoso.b2clogin.com/tenant-1/v2.0/";
    private const string Auth0 = "https://contoso.eu.auth0.com/";

    private static ClaimsPrincipal Token(string? issuer, params (string Type, string Value)[] claims)
    {
        var all = issuer is null ? claims : [("iss", issuer), .. claims];
        return new(new ClaimsIdentity(all.Select(c => new Claim(c.Type, c.Value)), "Bearer"));
    }

    public static TheoryData<string?, (string, string)[]> ApplicationTokens => new()
    {
        // Entra app-only tokens carry roles, no scp, and a sub that is the service principal (equal to oid).
        { EntraV2, [("oid", "sp-1"), ("sub", "sp-1"), ("roles", "Data.Read"), ("azp", "client-1")] },
        { EntraV1, [(MappedOid, "sp-1"), (ClaimTypes.NameIdentifier, "sp-1"), ("roles", "Data.Read"), ("appid", "client-1")] },
        { ExternalId, [("oid", "sp-1"), ("sub", "sp-1"), ("roles", "Data.Read")] },
        // No scp is enough on an Entra token, even if oid and sub ever differed.
        { EntraV2, [("oid", "sp-1"), ("sub", "other"), ("roles", "Data.Read")] },
        // oid equal to sub is enough on an Entra token, even if a scope claim were present.
        { EntraV2, [("oid", "sp-1"), ("sub", "sp-1"), ("scp", "Data.Read")] },
        // idtyp=app settles it for any issuer.
        { EntraV2, [("oid", "sp-1"), ("sub", "pairwise-1"), ("scp", "x"), ("idtyp", "app")] },
        { null, [("idtyp", "APP")] },
        // Another provider's client-credentials token: its subject is the client itself (RFC 9068).
        { Auth0, [("sub", "client-1@clients"), ("client_id", "client-1@clients")] },
        { Auth0, [("sub", "client-1"), ("azp", "client-1")] },
        // Ambiguous on Entra: a second sub equal to the oid fails closed.
        { EntraV2, [("oid", "sp-1"), ("sub", "pairwise-1"), (ClaimTypes.NameIdentifier, "sp-1"), ("scp", "x")] },
    };

    public static TheoryData<string?, (string, string)[]> PersonTokens => new()
    {
        // An Entra person: delegated scopes and a pairwise sub that differs from oid.
        { EntraV2, [("oid", "user-1"), ("sub", "pairwise-user-1"), ("scp", "Data.Read"), ("azp", "client-1")] },
        { EntraV1, [(MappedOid, "user-1"), (ClaimTypes.NameIdentifier, "pairwise-user-1"), (MappedScope, "Data.Read"), ("appid", "client-1")] },
        { ExternalId, [("oid", "user-1"), ("sub", "pairwise-user-1"), ("scp", "Data.Read")] },
        { EntraV2, [("oid", "user-1"), ("sub", "pairwise-user-1"), ("scp", "x"), ("idtyp", "user")] },
        // Azure AD B2C fills sub with the person's object id by default, so oid equals sub for every B2C user.
        { B2C, [("oid", "user-1"), ("sub", "user-1"), ("scp", "Data.Read"), ("azp", "client-1"), ("tfp", "b2c_1_signin")] },
        { B2C, [("oid", "user-1"), ("sub", "user-1")] },
        // Other providers often issue a person no scp.
        { Auth0, [("sub", "auth0|user-1"), ("azp", "client-1")] },
        { null, [("oid", "user-1")] },
        { null, [("sub", "user-1")] },
        { null, [("sub", "user-1"), ("roles", "Reader")] },
        // An issuer that only looks like Entra is not Entra.
        { "https://login.microsoftonline.com.evil.example/t/v2.0", [("oid", "user-1"), ("sub", "user-1")] },
        { "http://login.microsoftonline.com/t/v2.0", [("oid", "user-1"), ("sub", "user-1")] },
    };

    [Theory]
    [MemberData(nameof(ApplicationTokens))]
    public void IsApplicationOnly_ApplicationToken_IsTrue(string? issuer, (string, string)[] claims)
    {
        Token(issuer, claims).IsApplicationOnly().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(PersonTokens))]
    public void IsApplicationOnly_PersonsToken_IsFalse(string? issuer, (string, string)[] claims)
    {
        Token(issuer, claims).IsApplicationOnly().Should().BeFalse();
    }

    [Fact]
    public void IsApplicationOnly_IssuerStampedOnTheClaimsRatherThanAnIssClaim_IsStillRecognisedAsEntra()
    {
        // A handler that drops the iss claim still stamps the issuer on every claim it creates.
        var identity = new ClaimsIdentity(
            [new Claim("oid", "sp-1", ClaimValueTypes.String, EntraV2), new Claim("sub", "sp-1", ClaimValueTypes.String, EntraV2)],
            "Bearer");

        new ClaimsPrincipal(identity).IsApplicationOnly().Should().BeTrue();
    }

    [Fact]
    public void IsApplicationOnly_Unauthenticated_IsFalse()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("idtyp", "app"), new Claim("iss", EntraV2)]));

        anonymous.IsApplicationOnly().Should().BeFalse();
    }
}
