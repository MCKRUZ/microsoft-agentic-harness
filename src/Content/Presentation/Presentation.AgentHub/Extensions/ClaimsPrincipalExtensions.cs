using System.Security.Claims;

namespace Presentation.AgentHub.Extensions;

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
}
