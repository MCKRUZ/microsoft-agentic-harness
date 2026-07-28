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
/// is required. The capability-envelope resolver still resolves this principal to the fail-closed default
/// grant (it carries no <em>subject</em> claim), so an anonymous run is confined — the door is open, but the
/// room is empty. Never registered when a real scheme is configured.
/// </summary>
/// <remarks>
/// <para>
/// The principal carries a stable synthetic <c>oid</c> so the knowledge scope resolves to a real, consistent
/// owner. Without it the scope stayed null, and a null owner is treated as GLOBAL by
/// <c>PlannerScopeFilter.VisibleTo</c> — so every plan written on a developer's machine was a world-readable
/// global record. Contained (one synthetic principal, no cross-user leak), but this is a template consumers
/// clone, and "dev-only" is exactly how a stray global plan gets created and then confuses someone.
/// </para>
/// <para>
/// DELIBERATE DIVERGENCE from <c>DevAuthHandler</c>, which mints <c>oid</c> AND
/// <see cref="ClaimTypes.NameIdentifier"/>: <c>NameIdentifier</c> is the claim
/// <c>CapabilityEnvelopeResolver</c> reads as the envelope <em>subject</em>. Adding it here would make this
/// anonymous principal addressable by an <c>Envelopes:BySubject</c> grant, turning the fail-closed default
/// into something an operator could widen by name. Identity, yes; subject, no — do not "align" this by
/// adding <c>NameIdentifier</c>.
/// </para>
/// </remarks>
public sealed class AnonymousAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name under which this handler is registered.</summary>
    public const string SchemeName = "BundleApiAnonymous";

    /// <summary>
    /// The stable synthetic identity every anonymous-mode request runs as. Exposed so tests and consumers
    /// can assert on it rather than duplicating the literal.
    /// </summary>
    public const string AnonymousUserId = "anonymous-dev";

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
            [
                new Claim(ClaimTypes.Name, AnonymousUserId),
                // Stable identity so the knowledge scope is non-null and consistent. NOT NameIdentifier —
                // see the type remarks: that claim is the capability-envelope subject.
                new Claim("oid", AnonymousUserId),
            ],
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
