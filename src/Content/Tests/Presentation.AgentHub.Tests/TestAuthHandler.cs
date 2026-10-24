using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Stub authentication handler for integration tests.
/// Reads the <c>x-test-user</c> request header to determine the authenticated user's identity
/// (defaults to <c>"test-user"</c> when absent). Reads <c>x-test-roles</c>
/// (comma-separated) to populate role claims on the resulting principal.
///
/// Emits an <c>oid</c> claim so that
/// <see cref="Presentation.Common.Extensions.ClaimsPrincipalExtensions.GetUserId"/>
/// resolves correctly — Azure AD's object ID is read from the <c>oid</c> claim first.
///
/// Reads <c>x-test-claim-shape</c> to narrow the minted claim set. The default emits every claim,
/// which is convenient but hides attribution defects: an endpoint that only ever sees a principal
/// carrying <em>all</em> the identity claims cannot demonstrate that it reads the right one. Pass
/// <c>oid-only</c> or <c>sub-only</c> to mint the single-claim token shapes real providers issue,
/// <c>person</c> for a person's Entra token, or <c>app-only</c> / <c>app-idtyp</c> for an application's own token.
/// The default set names no issuer, so it is never read as an Entra application token.
///
/// Register as the default scheme in <c>ConfigureTestServices</c> to bypass Azure AD auth
/// while supporting per-test identity and role customisation via HTTP headers.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Authentication scheme name used to override JWT bearer in integration tests.</summary>
    public const string SchemeName = "TestAuth";

    /// <summary>HTTP header for controlling the authenticated user identity in tests.</summary>
    public const string UserIdHeader = "x-test-user";

    /// <summary>HTTP header for injecting role claims in tests (comma-separated values).</summary>
    public const string RolesHeader = "x-test-roles";

    /// <summary>
    /// HTTP header selecting which identity claims to mint: <c>oid-only</c>, <c>sub-only</c>, or
    /// absent for the full set.
    /// </summary>
    public const string ClaimShapeHeader = "x-test-claim-shape";

    /// <summary>The Entra issuer the <c>person</c> and <c>app-*</c> claim shapes carry.</summary>
    public const string EntraIssuer = "https://login.microsoftonline.com/test-tenant/v2.0";

    /// <summary>HTTP header carrying a tenant id, minted as a <c>tid</c> claim; absent mints none.</summary>
    public const string TenantHeader = "x-test-tenant";

    /// <inheritdoc/>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers[UserIdHeader].FirstOrDefault() ?? "test-user";
        var rolesHeader = Request.Headers[RolesHeader].ToString();
        var roles = string.IsNullOrWhiteSpace(rolesHeader)
            ? []
            : rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var claims = Request.Headers[ClaimShapeHeader].FirstOrDefault() switch
        {
            "oid-only" => [new Claim("oid", userId)],
            "sub-only" => [new Claim("sub", userId)],
            // #907 Entra token shapes. A person carries delegated scopes and a pairwise sub that differs from their oid; an
            // app-only token carries roles but no scp, and its sub is the service principal itself. A tenant may also turn
            // on idtyp=app.
            "person" =>
            [
                new Claim("iss", EntraIssuer), new Claim("oid", userId), new Claim("sub", $"pairwise-{userId}"),
                new Claim("scp", "access_as_user"),
            ],
            "app-only" =>
            [
                new Claim("iss", EntraIssuer), new Claim("oid", userId), new Claim("sub", userId),
                new Claim("roles", "access_as_application"),
            ],
            "app-idtyp" =>
            [
                new Claim("iss", EntraIssuer), new Claim("oid", userId), new Claim("sub", $"pairwise-{userId}"),
                new Claim("scp", "access_as_user"), new Claim("idtyp", "app"),
            ],
            _ => new List<Claim>
            {
                // ClaimsPrincipalExtensions.GetUserId() reads the "oid" claim first.
                // Emitting it here ensures hub ownership checks work in integration tests.
                new("oid", userId),
                new(ClaimTypes.NameIdentifier, userId),
                new(ClaimTypes.Name, userId),
                // Approver identity claim read by the escalation API
                // (EscalationConfig.ApproverClaimType default "preferred_username").
                new("preferred_username", userId),
            },
        };

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        if (Request.Headers[TenantHeader].FirstOrDefault() is { Length: > 0 } tenantId)
            claims.Add(new Claim("tid", tenantId));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
