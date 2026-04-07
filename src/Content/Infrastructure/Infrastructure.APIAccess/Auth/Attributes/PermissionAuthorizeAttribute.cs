using Domain.Common.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Infrastructure.APIAccess.Auth.Attributes;

/// <summary>
/// Authorization attribute that encodes <see cref="AuthPermissions"/> values into an
/// ASP.NET Core policy name, enabling permission-based access control on endpoints.
/// </summary>
/// <remarks>
/// Inherits from <see cref="AuthorizeAttribute"/> (ASP.NET Core) so the authorization
/// middleware automatically enforces the generated policy. The policy name encodes
/// permission enum values as integers joined by hyphens, prefixed with "Permission"
/// (e.g., <c>[PermissionAuthorize(Access, Admin)]</c> produces <c>"Permission0-2"</c>).
/// <para>
/// This encoding allows the authorization middleware to parse the policy name back
/// into individual permissions without requiring a separate policy per combination.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [PermissionAuthorize(AuthPermissions.Access, AuthPermissions.Admin)]
/// public async Task&lt;IResult&gt; AdminEndpoint() { ... }
/// </code>
/// </example>
public class PermissionAuthorizeAttribute : AuthorizeAttribute
{
    private const string PolicyPrefix = "Permission";

    /// <summary>
    /// Initializes a new instance requiring the specified permissions.
    /// </summary>
    /// <param name="permissions">One or more permissions that must all be satisfied.</param>
    public PermissionAuthorizeAttribute(params AuthPermissions[] permissions)
    {
        SetPolicy(permissions?.ToList() ?? []);
    }

    private void SetPolicy(List<AuthPermissions> permissions)
    {
        var permissionInts = permissions.Select(p => (int)p);
        Policy = PolicyPrefix + string.Join('-', permissionInts);
    }
}
