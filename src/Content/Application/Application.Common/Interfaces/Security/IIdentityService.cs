using Domain.Common;

namespace Application.Common.Interfaces.Security;

/// <summary>
/// Provides identity operations for user management and authorization checks.
/// Consumed by <c>AuthorizationBehavior</c> for role and policy verification.
/// </summary>
/// <remarks>
/// Implementation lives in Infrastructure (e.g., Azure AD B2C, Identity Server, Entra ID).
/// </remarks>
public interface IIdentityService
{
    /// <summary>Gets the display name for a user by their ID.</summary>
    Task<string?> GetUserNameAsync(string userId);

    /// <summary>Checks whether a user belongs to the specified role.</summary>
    Task<bool> IsInRoleAsync(string userId, string role);

    /// <summary>Checks whether a user satisfies the specified authorization policy.</summary>
    Task<bool> AuthorizeAsync(string userId, string policyName);

    /// <summary>Creates a new user account. Returns the new user's ID on success.</summary>
    Task<Result<string>> CreateUserAsync(string userName, string password);

    /// <summary>Deletes a user account.</summary>
    Task<Result> DeleteUserAsync(string userId);
}
