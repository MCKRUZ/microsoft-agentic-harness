using System.Security.Claims;
using Domain.Common.Constants;

namespace Infrastructure.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="ClaimsPrincipal"/> providing typed access
/// to application-specific claims defined in <see cref="ClaimConstants"/>.
/// </summary>
public static class ClaimExtensions
{
    /// <summary>
    /// Gets the application user ID from the claims principal.
    /// </summary>
    /// <returns>The user ID, or <c>null</c> if the claim is absent or empty.</returns>
    public static string? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirst(ClaimConstants.UserId)?.Value;
        return !string.IsNullOrEmpty(value) ? value : null;
    }

    /// <summary>
    /// Gets whether the user has administrative privileges.
    /// </summary>
    /// <returns><c>true</c> if the admin claim is present and set to "true"; <c>false</c> otherwise.</returns>
    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        bool.TryParse(principal.FindFirst(ClaimConstants.IsAdmin)?.Value, out var result) && result;

    /// <summary>
    /// Gets whether the user has agreed to terms and conditions.
    /// </summary>
    /// <returns><c>true</c> if the terms claim is present and set to "true"; <c>false</c> otherwise.</returns>
    public static bool HasAgreedToTerms(this ClaimsPrincipal principal) =>
        bool.TryParse(principal.FindFirst(ClaimConstants.AgreedToTerms)?.Value, out var result) && result;
}
