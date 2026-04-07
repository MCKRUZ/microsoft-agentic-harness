namespace Domain.Common.Constants;

/// <summary>
/// Application-specific claim types used in authentication and authorization.
/// Added to JWT tokens during authentication and read during authorization checks.
/// </summary>
/// <remarks>
/// Using constants instead of magic strings ensures consistency across the
/// application and makes it easier to refactor claim type names.
/// <para>
/// <code>
/// var claims = new[]
/// {
///     new Claim(ClaimConstants.UserId, userId),
///     new Claim(ClaimConstants.IsAdmin, "true")
/// };
/// </code>
/// </para>
/// </remarks>
public static class ClaimConstants
{
    /// <summary>Claim type for the application user identifier.</summary>
    public const string UserId = "app-user-id";

    /// <summary>Claim type indicating whether the user has administrative privileges.</summary>
    public const string IsAdmin = "app-user-is-admin";

    /// <summary>Claim type indicating whether the user has agreed to terms and conditions.</summary>
    public const string AgreedToTerms = "app-user-agreed-to-terms";
}
