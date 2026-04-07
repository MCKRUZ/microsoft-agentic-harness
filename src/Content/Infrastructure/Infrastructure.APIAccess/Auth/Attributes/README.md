# PermissionAuthorizeAttribute

## Overview

The `PermissionAuthorizeAttribute` is a custom authorization attribute that extends ASP.NET Core's `AuthorizeAttribute` to provide permission-based authorization for controllers and actions. It uses a policy-based approach to create authorization policies based on specific permissions defined in the `AuthPermissions` enum.

## Location
`Infrastructure.APIAccess/Auth/Attributes/PermissionAuthorizeAttribute.cs`

## Available Permissions

The attribute supports the following permissions defined in the `AuthPermissions` enum (`Domain.Common.Enums`):

- **Access** (0) - Basic access permission (automatically granted)
- **TermsAgreement** (1) - User has agreed to terms of service
- **Admin** (2) - Administrative access

## Usage

### Basic Usage

Apply the attribute to controllers or individual actions to require specific permissions:

```csharp
// Require a single permission
[PermissionAuthorize(AuthPermissions.Access)]
public class MyController : ControllerBase
{
    // All actions require Access permission
}

// Require multiple permissions
[PermissionAuthorize(AuthPermissions.Access, AuthPermissions.TermsAgreement)]
public class SecureController : ControllerBase
{
    // All actions require both Access AND TermsAgreement
}
```

### Action-Level Authorization

You can override controller-level permissions at the action level:

```csharp
[PermissionAuthorize(AuthPermissions.Access)]
public class UserController : ControllerBase
{
    // Requires Access permission (inherited from controller)
    [HttpGet]
    public IActionResult GetUserProfile() { }

    // Requires Admin permission (overrides controller level)
    [PermissionAuthorize(AuthPermissions.Admin)]
    [HttpDelete]
    public IActionResult DeleteUser(int userId) { }

    // Requires both Access and TermsAgreement
    [PermissionAuthorize(AuthPermissions.Access, AuthPermissions.TermsAgreement)]
    [HttpPost("agree-terms")]
    public IActionResult AgreeToTerms() { }
}
```

### Combining with Standard Authorization

The attribute can be combined with standard authorization attributes:

```csharp
[Authorize] // Standard authentication required
[PermissionAuthorize(AuthPermissions.Admin)]
public class AdminController : ControllerBase
{
    // User must be authenticated AND have Admin permission
}
```

## How It Works

1. **Policy Generation**: The attribute converts permission enums into a policy string with the format `"Permission{perm1}-{perm2}..."` where each permission is represented by its integer value.

2. **Policy Names**: For example:
   - `[PermissionAuthorize(AuthPermissions.Access)]` creates policy `"Permission0"`
   - `[PermissionAuthorize(AuthPermissions.Admin, AuthPermissions.Access)]` creates policy `"Permission2-0"`

3. **Authorization Flow**: The attribute relies on ASP.NET Core's policy-based authorization system. You must configure corresponding authorization policies in your application's startup configuration.

## Implementation Details

### Constructor
```csharp
public PermissionAuthorizeAttribute(params AuthPermissions[] permissions)
{
    SetPolicy(permissions?.ToList() ?? []);
}
```

### Policy Generation
The attribute converts permissions to integer values and creates a policy name:
```csharp
private void SetPolicy(List<AuthPermissions> permissions)
{
    var permissionInts = permissions.Select(p => (int)p);
    Policy = PolicyPrefix + string.Join('-', permissionInts);
}
```

## Configuration Requirements

To use this attribute effectively, you need to configure authorization policies in your `Program.cs` or `Startup.cs`:

```csharp
builder.Services.AddAuthorization(options =>
{
    // Configure policies for individual permissions
    options.AddPolicy("Permission0", policy =>
        policy.AddRequirements(new PermissionRequirement(AuthPermissions.Access)));

    options.AddPolicy("Permission1", policy =>
        policy.AddRequirements(new PermissionRequirement(AuthPermissions.TermsAgreement)));

    options.AddPolicy("Permission2", policy =>
        policy.AddRequirements(new PermissionRequirement(AuthPermissions.Admin)));

    // Configure combined permission policies
    options.AddPolicy("Permission2-0", policy =>
        policy.AddRequirements(new PermissionRequirement(AuthPermissions.Admin))
              .AddRequirements(new PermissionRequirement(AuthPermissions.Access)));
});
```

## Dependencies

- **PermissionRequirement**: The authorization requirement class used to define what permission is being checked
- **PermissionAuthHandler**: The authorization handler that evaluates the permission requirements
- **AuthPermissions enum**: Defines the available permission types (in `Domain.Common.Enums`)
- **ASP.NET Core Authorization**: Uses the built-in policy-based authorization system (`Microsoft.AspNetCore.Authorization.AuthorizeAttribute`)

## Error Handling

When a user lacks the required permissions, ASP.NET Core will return:
- **401 Unauthorized** if the user is not authenticated
- **403 Forbidden** if the user is authenticated but lacks the required permissions

If an unconfigured permission is encountered, the `PermissionAuthHandler` will throw an `ArgumentOutOfRangeException`.

## Best Practices

1. **Apply at Controller Level**: Use the most restrictive permission at the controller level and only override with more permissive policies when necessary

2. **Consistent Naming**: Ensure your policy configuration matches the generated policy names exactly

3. **Combining Permissions**: When requiring multiple permissions, ensure users have ALL specified permissions (AND logic)

4. **Permission Validation**: Make sure all permissions in the `AuthPermissions` enum are properly handled in the `PermissionAuthHandler`

5. **Null Safety**: The attribute automatically handles null permission arrays by defaulting to an empty list

## Implementation Notes

- The attribute automatically handles null permission arrays by defaulting to an empty list
- Policy names are prefixed with "Permission" to avoid conflicts with other policies
- Multiple permissions are joined with hyphens in the policy name
- The attribute inherits from `Microsoft.AspNetCore.Authorization.AuthorizeAttribute`, so it maintains all standard authorization functionality
- Works seamlessly with the existing `PermissionAuthHandler` and `PermissionRequirement` classes
