using Domain.Common.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Infrastructure.APIAccess.Auth.Requirements;

/// <summary>
/// ASP.NET Core authorization requirement that carries a single <see cref="AuthPermissions"/> value.
/// Evaluated by <see cref="Handlers.PermissionAuthHandler"/>.
/// </summary>
public class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Gets the permission that must be satisfied.
    /// </summary>
    public AuthPermissions Permission { get; }

    /// <summary>
    /// Initializes a new instance with the specified permission.
    /// </summary>
    /// <param name="permission">The required permission level.</param>
    public PermissionRequirement(AuthPermissions permission)
    {
        Permission = permission;
    }
}
