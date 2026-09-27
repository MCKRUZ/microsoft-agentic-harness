using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Presentation.AgentHub.Auth;

/// <summary>
/// Authentication handler for a self-hosted, non-Azure deployment with sign-in turned off
/// (<see cref="AuthBypassPolicy.IsBypassed"/> via <c>Auth:AllowOutsideDevelopment</c> — issue #591).
/// Auto-authenticates every request as a synthetic caller with a stable identity but
/// <strong>no elevated roles at all</strong>.
/// </summary>
/// <remarks>
/// Deliberately NOT <see cref="DevAuthHandler"/>. That handler grants its synthetic principal
/// escalation-admin, change-proposal-admin, and drift/registry-operate roles — appropriate for a
/// developer exercising every code path on a local machine, and <see cref="DevAuthHandler"/>'s own
/// doc comment already warns that "a consumer who copies this handler ... inherits an
/// operator-capable principal." Reusing it for a real, network-reachable deployment would grant
/// that same operator-level power to anyone who reaches the port, not just "no sign-in for normal
/// use" — a materially larger blast radius than the "no auth" trade-off documented in
/// <c>documentation/onboarding/18-self-hosted-docker.html</c> actually describes. This handler
/// grants only what an ordinary agent-turn caller needs and nothing an administrator would.
/// </remarks>
internal sealed class SelfHostedAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "SelfHosted";
    internal const string CallerId = "self-hosted-caller";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, CallerId),
            new Claim(ClaimTypes.Name, "Self-Hosted Caller"),
            new Claim("oid", CallerId),
            new Claim("preferred_username", CallerId),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
