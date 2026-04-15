using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Stub authentication handler for integration tests.
/// Authenticates all requests as a fixed test user without validating any token.
/// Register as the default scheme in <c>ConfigureTestServices</c> to bypass Azure AD auth.
/// </summary>
/// <remarks>
/// Replaced with a full implementation in section-07 that supports per-test claim customization.
/// </remarks>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Authentication scheme name used to override JWT bearer in integration tests.</summary>
    public const string SchemeName = "TestAuth";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[] { new Claim(ClaimTypes.Name, "test-user") };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
