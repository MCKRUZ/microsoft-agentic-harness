namespace Domain.AI.Permissions;

/// <summary>
/// Represents the outcome of a permission resolution for a tool invocation.
/// </summary>
public enum PermissionBehaviorType
{
    /// <summary>Tool use is allowed without user confirmation.</summary>
    Allow,
    /// <summary>Tool use is denied. Agent should not retry.</summary>
    Deny,
    /// <summary>Tool use requires explicit user/caller confirmation before proceeding.</summary>
    Ask
}
