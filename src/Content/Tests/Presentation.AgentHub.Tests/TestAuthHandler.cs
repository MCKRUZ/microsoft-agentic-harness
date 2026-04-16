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
/// <see cref="Presentation.AgentHub.Extensions.ClaimsPrincipalExtensions.GetUserId"/>
/// resolves correctly — Azure AD's object ID is read from the <c>oid</c> claim first.
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

    /// <inheritdoc/>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers[UserIdHeader].FirstOrDefault() ?? "test-user";
        var rolesHeader = Request.Headers[RolesHeader].ToString();
        var roles = string.IsNullOrWhiteSpace(rolesHeader)
            ? []
            : rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var claims = new List<Claim>
        {
            // ClaimsPrincipalExtensions.GetUserId() reads the "oid" claim first.
            // Emitting it here ensures hub ownership checks work in integration tests.
            new("oid", userId),
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId),
        };

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
