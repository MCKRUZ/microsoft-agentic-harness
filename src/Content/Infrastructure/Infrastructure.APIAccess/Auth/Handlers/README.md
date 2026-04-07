# PermissionAuthHandler

## Overview

The `PermissionAuthHandler` is an ASP.NET Core authorization handler that evaluates permission-based authorization requirements. It extends `AuthorizationHandler<PermissionRequirement>` and implements the logic to determine whether a user has the necessary permissions based on the `AuthPermissions` enum.

## Location
`Infrastructure.APIAccess/Auth/Handlers/PermissionAuthHandler.cs`

## Purpose

This handler is responsible for:
- Validating that users are authenticated before checking permissions
- Evaluating specific permission requirements against the current user
- Working with ASP.NET Core's authorization infrastructure to grant or deny access

## Supported Permissions

The handler evaluates the following permissions defined in `AuthPermissions` enum (`Domain.Common.Enums`):

- **Access** (0) - Always granted to authenticated users
- **TermsAgreement** (1) - Granted if the user has agreed to terms
- **Admin** (2) - Granted if the user has administrative privileges

## Implementation

### Class Definition
```csharp
public class PermissionAuthHandler : AuthorizationHandler<PermissionRequirement>
```

### Core Authorization Logic

The handler overrides the `HandleRequirementAsync` method to implement permission checking:

```csharp
protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
{
    if (context.User.Identity is null || !context.User.Identity.IsAuthenticated)
        return Task.CompletedTask;

    if (PermissionRequirementsMet(requirement.Permission, context.User))
        context.Succeed(requirement);

    return Task.CompletedTask;
}
```

### Permission Evaluation Logic

The `PermissionRequirementsMet` method uses a switch expression for concise pattern matching:

```csharp
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
```

## Permission Logic Details

### Access Permission
- **Logic**: Always returns `true` for authenticated users
- **Use Case**: Basic access control to ensure user is authenticated
- **Implementation**: `return true;`

### TermsAgreement Permission
- **Logic**: Checks if user has agreed to terms of service
- **Use Case**: Protecting features that require terms acceptance
- **Implementation**: `user.HasAgreedToTerms()`
- **Dependency**: `ClaimExtensions.HasAgreedToTerms()` in `Infrastructure.Common.Extensions`

### Admin Permission
- **Logic**: Checks if user has administrative privileges
- **Use Case**: Restricting administrative functions to authorized users
- **Implementation**: `user.IsAdmin()`
- **Dependency**: `ClaimExtensions.IsAdmin()` in `Infrastructure.Common.Extensions`

## Dependencies

### Required Extension Methods
The handler depends on `ClaimExtensions` from `Infrastructure.Common.Extensions`:

```csharp
public static class ClaimExtensions
{
    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        bool.TryParse(principal.FindFirst(ClaimConstants.IsAdmin)?.Value, out var result) && result;

    public static bool HasAgreedToTerms(this ClaimsPrincipal principal) =>
        bool.TryParse(principal.FindFirst(ClaimConstants.AgreedToTerms)?.Value, out var result) && result;
}
```

### Required Namespaces
```csharp
using Domain.Common.Enums;
using Infrastructure.APIAccess.Auth.Requirements;
using Infrastructure.Common.Extensions;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
```

## Registration

To use this handler, register it in your DI container:

```csharp
builder.Services.AddAuthorization(options =>
{
    // Register policies that use this handler
    options.AddPolicy("Permission0", policy =>
        policy.AddRequirements(new PermissionRequirement(AuthPermissions.Access)));
});

// Register the handler
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthHandler>();
```

## Error Handling

### Unauthenticated Users
- **Behavior**: Handler returns without calling `context.Succeed()`
- **Result**: Access is denied (401 Unauthorized)
- **Implementation**: Early return if `context.User.Identity` is null or not authenticated

### Unconfigured Permissions
- **Behavior**: Throws `ArgumentOutOfRangeException`
- **Use Case**: Developer error when new permissions are added to enum but not handled
- **Implementation**: Default arm in switch expression

```csharp
_ => throw new ArgumentOutOfRangeException(
    nameof(permission), permission, "Permission not configured"),
```

## Integration with Authorization System

### Flow
1. **Authentication**: User is authenticated
2. **Requirement**: `PermissionRequirement` is created with specific permission
3. **Evaluation**: Handler is called to evaluate the requirement
4. **Decision**: Handler calls `context.Succeed(requirement)` if permission is granted
5. **Result**: Authorization succeeds or fails based on handler evaluation

### Success Conditions
- User is authenticated AND
- Permission requirements are met according to the business logic

### Failure Conditions
- User is not authenticated
- Permission requirements are not met
- Handler doesn't call `context.Succeed()`

## Best Practices

1. **Extension Method Implementation**: Ensure `HasAgreedToTerms()` and `IsAdmin()` are properly implemented and handle edge cases

2. **Permission Enum Synchronization**: When adding new permissions to `AuthPermissions` enum, update the switch expression in `PermissionRequirementsMet`

3. **Error Handling**: Consider adding logging for authorization decisions for debugging and audit purposes

4. **Performance**: The handler uses static methods and should be efficient for repeated calls

5. **Testing**: Unit test each permission case, including edge cases and error conditions

## Example Usage in Controllers

```csharp
[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    [PermissionAuthorize(AuthPermissions.Admin)]
    [HttpPost("admin-action")]
    public IActionResult AdminAction()
    {
        // This action will only be executed if:
        // 1. User is authenticated
        // 2. PermissionAuthHandler grants Admin permission
        return Ok("Admin action completed");
    }

    [PermissionAuthorize(AuthPermissions.TermsAgreement)]
    [HttpPost("user-action")]
    public IActionResult UserAction()
    {
        // This action will only be executed if:
        // 1. User is authenticated
        // 2. PermissionAuthHandler confirms user has agreed to terms
        return Ok("User action completed");
    }
}
```

## Troubleshooting

### Common Issues

1. **403 Forbidden**: Check that the user has the required claims/permissions and that the extension methods are working correctly

2. **500 Error**: Look for `ArgumentOutOfRangeException` in logs - this indicates a permission was added to the enum but not handled in the switch expression

3. **Extension Method Not Found**: Ensure the `Infrastructure.Common.Extensions` namespace is imported and the extension methods are properly implemented

4. **Handler Not Called**: Verify that the handler is registered in the DI container and that policies are configured correctly

## Thread Safety

The handler is designed to be thread-safe:
- Uses static methods for permission evaluation
- No instance state that could be corrupted
- Safe for concurrent use in multi-threaded environments
