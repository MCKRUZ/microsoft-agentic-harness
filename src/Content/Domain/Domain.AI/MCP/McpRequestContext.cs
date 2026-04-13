using System.Security.Claims;

namespace Domain.AI.MCP;

/// <summary>
/// Carries per-request context for MCP resource operations, including the caller's auth principal.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsAuthenticated"/> is the canonical auth gate used by MCP resource providers.
/// A context is authenticated when its <see cref="Principal"/> carries a validated identity
/// (i.e., <c>ClaimsIdentity.AuthenticationType</c> is non-null/non-empty and
/// <c>Identity.IsAuthenticated</c> is true).
/// </para>
/// <para>
/// Use <see cref="FromPrincipal"/> to construct an authenticated context from a JWT-validated
/// <see cref="ClaimsPrincipal"/>, or <see cref="Unauthenticated"/> for anonymous / test contexts.
/// </para>
/// </remarks>
public sealed class McpRequestContext
{
    /// <summary>Gets the authenticated principal, or <c>null</c> if the caller is anonymous.</summary>
    public ClaimsPrincipal? Principal { get; init; }

    /// <summary>
    /// Gets whether the request carries a valid authenticated identity.
    /// Returns <c>true</c> only when <see cref="Principal"/> is non-null and its primary identity
    /// reports <c>IsAuthenticated == true</c>.
    /// </summary>
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    /// <summary>A pre-built unauthenticated context with no principal. Suitable for anonymous access or tests.</summary>
    public static McpRequestContext Unauthenticated { get; } = new();

    /// <summary>Creates an authenticated context wrapping the given <paramref name="principal"/>.</summary>
    /// <param name="principal">A JWT-validated <see cref="ClaimsPrincipal"/> with <c>IsAuthenticated == true</c>.</param>
    public static McpRequestContext FromPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new() { Principal = principal };
    }
}
