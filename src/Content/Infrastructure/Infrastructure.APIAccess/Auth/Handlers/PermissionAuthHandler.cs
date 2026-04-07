using Domain.Common.Enums;
using Infrastructure.APIAccess.Auth.Requirements;
using Infrastructure.Common.Extensions;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace Infrastructure.APIAccess.Auth.Handlers;

/// <summary>
/// Authorization handler that evaluates <see cref="PermissionRequirement"/> against
/// the current user's claims using <see cref="ClaimExtensions"/>.
/// </summary>
/// <remarks>
/// Permission checks:
/// <list type="bullet">
///   <item><see cref="AuthPermissions.Access"/> — always granted for authenticated users</item>
///   <item><see cref="AuthPermissions.TermsAgreement"/> — requires terms acceptance claim</item>
///   <item><see cref="AuthPermissions.Admin"/> — requires admin claim</item>
/// </list>
/// <para>
/// Register in DI:
/// <code>
/// services.AddSingleton&lt;IAuthorizationHandler, PermissionAuthHandler&gt;();
/// </code>
/// </para>
/// </remarks>
public class PermissionAuthHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc/>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity is null || !context.User.Identity.IsAuthenticated)
            return Task.CompletedTask;

        if (PermissionRequirementsMet(requirement.Permission, context.User))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }

    private static bool PermissionRequirementsMet(AuthPermissions permission, ClaimsPrincipal user)
    {
        return permission switch
        {
            AuthPermissions.Access => true,
            AuthPermissions.TermsAgreement => user.HasAgreedToTerms(),
            AuthPermissions.Admin => user.IsAdmin(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(permission), permission, "Permission not configured"),
        };
    }
}
