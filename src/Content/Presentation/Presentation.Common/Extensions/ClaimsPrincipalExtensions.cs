using System.Security.Claims;

namespace Presentation.Common.Extensions;

/// <summary>Extension methods for <see cref="ClaimsPrincipal"/> to simplify Azure AD claim access.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Returns the Azure AD object ID (OID) of the authenticated user.
    /// Throws <see cref="InvalidOperationException"/> if the claim is absent —
    /// this should never occur for endpoints protected by <c>[Authorize]</c> with
    /// a valid Azure AD token.
    /// </summary>
    public static string GetUserId(this ClaimsPrincipal principal)
    {
        // Azure AD tokens include the object ID in either the standard "oid" claim
        // or the namespaced "http://schemas.microsoft.com/identity/claims/objectidentifier" claim.
        var oid = principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

        if (string.IsNullOrEmpty(oid))
            throw new InvalidOperationException("The 'oid' claim is missing from the authenticated user's token.");

        return oid;
    }

    /// <summary>
    /// Returns the Azure AD tenant ID (<c>tid</c>) of the authenticated user, or <c>null</c> when
    /// the token carries no tenant claim. Unlike <see cref="GetUserId"/> this does not throw —
    /// tenant is optional (single-tenant deployments and the dev auth bypass have no <c>tid</c>),
    /// and the knowledge scope falls back to its configured default tenant when this is null.
    /// </summary>
    public static string? GetTenantId(this ClaimsPrincipal principal)
    {
        // Azure AD tokens carry the tenant ID in the standard "tid" claim or the namespaced
        // "http://schemas.microsoft.com/identity/claims/tenantid" claim.
        var tid = principal.FindFirstValue("tid")
            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");

        return string.IsNullOrEmpty(tid) ? null : tid;
    }

    /// <summary>
    /// Returns the authenticated user's object ID, or <c>null</c> when the principal is
    /// unauthenticated or carries no <c>oid</c> claim. Non-throwing companion to
    /// <see cref="GetUserId"/> for entry-point code that runs before authorization is guaranteed.
    /// </summary>
    public static string? GetUserIdOrNull(this ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return null;

        var oid = principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

        return string.IsNullOrEmpty(oid) ? null : oid;
    }
}
