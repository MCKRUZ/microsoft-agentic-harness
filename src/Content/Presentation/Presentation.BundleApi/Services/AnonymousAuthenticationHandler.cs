using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Presentation.BundleApi.Services;

/// <summary>
/// Authentication handler used only in the explicit anonymous opt-in
/// (<c>AppConfig:AI:BundleExecution:Auth:AllowAnonymous=true</c>). It authenticates every request as a fixed
/// synthetic development principal, so the controller's <c>[Authorize]</c> is satisfied while no real identity
/// is required. The capability-envelope resolver then resolves this principal to the fail-closed default
/// grant (it carries no subject), so an anonymous run is still confined — the door is open, but the room is
/// empty. Never registered when a real scheme is configured.
/// </summary>
public sealed class AnonymousAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name under which this handler is registered.</summary>
    public const string SchemeName = "BundleApiAnonymous";

    /// <summary>Initializes a new <see cref="AnonymousAuthenticationHandler"/>.</summary>
    public AnonymousAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "anonymous-dev")], SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
