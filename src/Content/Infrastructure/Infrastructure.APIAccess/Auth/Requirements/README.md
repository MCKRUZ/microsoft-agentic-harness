# PermissionRequirement

## Overview

The `PermissionRequirement` class implements ASP.NET Core's `IAuthorizationRequirement` interface to define permission-based authorization requirements. It serves as a simple data container that specifies which permission is required for authorization.

## Location
`Infrastructure.APIAccess/Auth/Requirements/PermissionRequirement.cs`

## Purpose

The `PermissionRequirement` class is responsible for:
- Encapsulating a specific permission requirement
- Providing the data needed by `PermissionAuthHandler` to evaluate authorization
- Integrating with ASP.NET Core's policy-based authorization system

## Class Definition

```csharp
public class PermissionRequirement : IAuthorizationRequirement
{
    public AuthPermissions Permission { get; }

    public PermissionRequirement(AuthPermissions permission)
    {
        Permission = permission;
    }
}
```

## Implementation Details

### Interface Implementation
The class implements `IAuthorizationRequirement`, which is a marker interface from ASP.NET Core's authorization system. This interface has no members but signals to the authorization framework that this class represents an authorization requirement.

### Properties
- **Permission**: Gets the `AuthPermissions` enum value that represents the required permission
- **Access Modifier**: Getter-only property ensures the permission cannot be changed after creation

### Constructor
- **Parameter**: `AuthPermissions permission` - The permission that must be satisfied
- **Validation**: No validation is performed in the constructor; validation happens in the handler

## Supported Permissions

The requirement supports any permission defined in the `AuthPermissions` enum (`Domain.Common.Enums`):

- **Access** (0) - Basic access permission
- **TermsAgreement** (1) - User has agreed to terms of service
- **Admin** (2) - Administrative access

## Usage

### Creating Requirements

```csharp
// Create requirement for Admin permission
var adminRequirement = new PermissionRequirement(AuthPermissions.Admin);

// Create requirement for Terms Agreement
var termsRequirement = new PermissionRequirement(AuthPermissions.TermsAgreement);

// Create requirement for basic Access
var accessRequirement = new PermissionRequirement(AuthPermissions.Access);
```

### Using in Authorization Policies

```csharp
builder.Services.AddAuthorization(options =>
{
    // Policy requiring Admin permission
    options.AddPolicy("AdminOnly", policy =>
        policy.AddRequirements(new PermissionRequirement(AuthPermissions.Admin)));

    // Policy requiring Terms Agreement
    options.AddPolicy("TermsRequired", policy =>
        policy.AddRequirements(new PermissionRequirement(AuthPermissions.TermsAgreement)));

    // Policy requiring basic Access
    options.AddPolicy("AuthenticatedUsers", policy =>
        policy.AddRequirements(new PermissionRequirement(AuthPermissions.Access)));
});
```

### Using with PermissionAuthorizeAttribute

The `PermissionAuthorizeAttribute` creates these requirements internally:

```csharp
// This attribute internally creates:
// new PermissionRequirement(AuthPermissions.Admin)
[PermissionAuthorize(AuthPermissions.Admin)]
public class AdminController : ControllerBase
{
    // ...
}
```

## Integration with Authorization Flow

### Complete Flow
1. **Requirement Creation**: `PermissionRequirement` is instantiated with specific permission
2. **Policy Registration**: Requirement is added to an authorization policy
3. **Attribute Application**: `PermissionAuthorizeAttribute` references the policy by name
4. **Handler Evaluation**: `PermissionAuthHandler` receives the requirement and evaluates it
5. **Authorization Decision**: Handler determines if the requirement is satisfied

### Example Integration

```csharp
// 1. Define the policy with requirement
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Permission2", policy =>
        policy.AddRequirements(new PermissionRequirement(AuthPermissions.Admin)));
});

// 2. Apply the policy via attribute
[PermissionAuthorize(AuthPermissions.Admin)] // Creates "Permission2" policy
public class AdminController : ControllerBase
{
    // 3. Handler evaluates the requirement
    // 4. Access is granted or denied based on evaluation
}
```

## Dependencies

### Required Namespaces
```csharp
using Domain.Common.Enums;
using Microsoft.AspNetCore.Authorization;
```

### Related Classes
- **PermissionAuthHandler**: Evaluates this requirement
- **PermissionAuthorizeAttribute**: Creates requirements via policy generation
- **AuthPermissions enum**: Defines available permission values (`Domain.Common.Enums`)

## Benefits of This Design

### 1. Type Safety
- Uses strongly-typed enum instead of string-based permissions
- Compile-time checking for valid permission values
- IntelliSense support for available permissions

### 2. Extensibility
- Easy to add new permissions to the enum
- Handler automatically supports new permissions when switch is updated
- No changes needed to this class when adding permissions

### 3. Integration
- Seamlessly works with ASP.NET Core's authorization infrastructure
- Follows established patterns for custom requirements
- Compatible with policy-based and attribute-based authorization

### 4. Simplicity
- Minimal code with clear intent
- No complex validation logic (handled by auth handler)
- Immutable by design (getter-only property)

## Best Practices

### 1. Requirement Creation
```csharp
// Good: Create requirements with clear intent
var adminRequirement = new PermissionRequirement(AuthPermissions.Admin);

// Avoid: Creating requirements with invalid or unknown permissions
// (This would throw at runtime in the handler)
```

### 2. Policy Naming
```csharp
// Good: Use descriptive policy names that indicate the requirement
options.AddPolicy("AdminAccess", policy =>
    policy.AddRequirements(new PermissionRequirement(AuthPermissions.Admin)));

// Consistent with PermissionAuthorizeAttribute naming:
// "Permission2" for Admin permission
```

### 3. Handler Integration
Ensure your `PermissionAuthHandler` can handle all possible permission values:

```csharp
private static bool PermissionRequirementsMet(AuthPermissions permission, ClaimsPrincipal user)
{
    return permission switch
    {
        AuthPermissions.Access => true,
        AuthPermissions.TermsAgreement => user.HasAgreedToTerms(),
        AuthPermissions.Admin => user.IsAdmin(),

        // Important: Handle new permissions here
        AuthPermissions.NewPermission => user.HasNewPermission(),

        _ => throw new ArgumentOutOfRangeException(
            nameof(permission), permission, "Permission not configured"),
    };
}
```

## Error Handling

### Design Philosophy
This class follows the "fail-fast" principle for invalid permissions:
- No validation in constructor (performance)
- Validation occurs in handler during evaluation
- Clear error messages for unhandled permissions

### Common Scenarios

**Invalid Permission Handling:**
```csharp
// This will compile but fail at runtime if handler doesn't support it
var unknownPermission = (AuthPermissions)999;
var requirement = new PermissionRequirement(unknownPermission);

// Handler will throw: ArgumentOutOfRangeException
```

**Null Permission Handling:**
```csharp
// This won't compile due to enum requirement
// var requirement = new PermissionRequirement(null);
```

## Testing Considerations

### Unit Testing Requirements
```csharp
[Fact]
public void PermissionRequirement_ShouldStoreCorrectPermission()
{
    // Arrange
    var expectedPermission = AuthPermissions.Admin;

    // Act
    var requirement = new PermissionRequirement(expectedPermission);

    // Assert
    Assert.Equal(expectedPermission, requirement.Permission);
}
```

### Integration Testing
- Test requirement creation and evaluation in authorization policies
- Verify that requirements are properly passed to handlers
- Test edge cases with invalid permissions

## Thread Safety

The class is inherently thread-safe:
- Immutable after construction
- No mutable state
- Safe for use in concurrent authorization scenarios
- No static state that could cause race conditions

## Performance Characteristics

- **Memory Allocation**: Minimal (single enum field)
- **Construction Cost**: Very low
- **Evaluation Cost**: Determined by handler, not this class
- **Garbage Collection**: Small object, short-lived in most scenarios
