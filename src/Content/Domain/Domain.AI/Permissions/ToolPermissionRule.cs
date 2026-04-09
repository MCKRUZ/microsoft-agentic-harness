namespace Domain.AI.Permissions;

/// <summary>
/// A single permission rule that matches tool invocations by name and optional operation pattern.
/// Rules are evaluated in priority order during the 3-phase resolution algorithm.
/// </summary>
/// <param name="ToolPattern">Glob pattern matching tool names (e.g., "file_system", "bash:*", "*").</param>
/// <param name="OperationPattern">Optional glob pattern matching operations (e.g., "read", "write:*"). Null matches all operations.</param>
/// <param name="Behavior">The permission behavior when this rule matches.</param>
/// <param name="Source">Where this rule originated.</param>
/// <param name="Priority">Evaluation priority. Lower values are checked first.</param>
/// <param name="IsBypassImmune">When true, this rule cannot be overridden by bypass/auto-approve modes.</param>
public sealed record ToolPermissionRule(
    string ToolPattern,
    string? OperationPattern,
    PermissionBehaviorType Behavior,
    PermissionRuleSource Source,
    int Priority,
    bool IsBypassImmune = false);
