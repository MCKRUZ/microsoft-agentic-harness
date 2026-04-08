namespace Domain.Common.Config.Azure;

/// <summary>
/// Azure AD B2C authentication configuration.
/// When <see cref="Instance"/> is null, B2C authentication is skipped
/// and basic JWT Bearer auth is used instead.
/// </summary>
public class AzureADB2CConfig
{
    /// <summary>
    /// Gets or sets the Azure AD B2C instance URL (e.g., "https://yourtenant.b2clogin.com").
    /// </summary>
    public string? Instance { get; set; }

    /// <summary>
    /// Gets or sets the Azure AD B2C tenant domain.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Gets or sets the sign-in/sign-up policy ID.
    /// </summary>
    public string? SignUpSignInPolicyId { get; set; }

    /// <summary>
    /// Gets or sets the signed-out callback path.
    /// </summary>
    public string? SignedOutCallbackPath { get; set; }
}
