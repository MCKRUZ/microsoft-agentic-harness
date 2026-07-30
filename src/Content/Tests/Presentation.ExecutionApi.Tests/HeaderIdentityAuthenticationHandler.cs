using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Test authentication scheme that mints a principal from request headers, so one host can serve
/// several distinct callers. Returns <c>NoResult</c> when no header is present, which is what makes
/// the "no identity" case a genuine 401 rather than a rigged one.
/// </summary>
internal sealed class HeaderIdentityAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestIdentity";
    public const string UserHeader = "X-Test-Oid";
    public const string SubHeader = "X-Test-Sub";
    public const string TenantHeader = "X-Test-Tid";

    /// <summary>Mints two conflicting <c>oid</c> claims — an authenticated but unresolvable caller.</summary>
    public const string AmbiguousHeader = "X-Test-Ambiguous";

    /// <summary>
    /// Mints the caller's approver identity, under the claim <c>EscalationConfig.ApproverClaimType</c>
    /// selects by default. Separate from <see cref="UserHeader"/> on purpose: ownership and roster
    /// identity are different claims that resolve to different strings for the same person, and a test
    /// harness that conflated them would hide the mismatch that makes a self-approval check silently
    /// match nothing.
    /// </summary>
    public const string ApproverHeader = "X-Test-Approver";

    public HeaderIdentityAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey(AmbiguousHeader))
            return Task.FromResult(Success(
                [new Claim("oid", "victim"), new Claim("oid", "attacker")]));

        var oid = Request.Headers[UserHeader].ToString();
        var sub = Request.Headers[SubHeader].ToString();
        if (string.IsNullOrEmpty(oid) && string.IsNullOrEmpty(sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(oid))
            claims.Add(new Claim("oid", oid));
        if (!string.IsNullOrEmpty(sub))
            claims.Add(new Claim("sub", sub));

        var tid = Request.Headers[TenantHeader].ToString();
        if (!string.IsNullOrEmpty(tid))
            claims.Add(new Claim("tid", tid));

        var approver = Request.Headers[ApproverHeader].ToString();
        if (!string.IsNullOrEmpty(approver))
            claims.Add(new Claim("preferred_username", approver));

        return Task.FromResult(Success(claims));
    }

    private static AuthenticateResult Success(IEnumerable<Claim> claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
