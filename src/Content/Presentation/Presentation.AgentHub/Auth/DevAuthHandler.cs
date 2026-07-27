using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Presentation.AgentHub.Auth;

/// <summary>
/// Development-only authentication handler that auto-authenticates every request
/// as a synthetic "dev user". Never registered outside of Development + Auth:Disabled=true.
/// </summary>
internal sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "Dev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "dev-user"),
            new Claim(ClaimTypes.Name, "Dev User"),
            new Claim("oid", "dev-user"),
            // Approver identity claim read by the escalation API (EscalationConfig.ApproverClaimType
            // default). Rosters targeting "dev-user" make the approval loop exercisable locally.
            new Claim("preferred_username", "dev-user"),
            new Claim(ClaimTypes.Role, "AgentHub.Traces.ReadAll"),
            new Claim(ClaimTypes.Role, "AgentHub.EvalDashboard.Read"),
            new Claim(ClaimTypes.Role, "AgentHub.Foresight.Observe"),
            new Claim(ClaimTypes.Role, Presentation.Common.Escalations.EscalationsController.DecideRole),
            new Claim(ClaimTypes.Role, Presentation.Common.Escalations.EscalationsController.AdminRole),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
