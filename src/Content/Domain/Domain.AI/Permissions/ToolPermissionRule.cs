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
/// <param name="IsAuthoritativeBaseline">
/// When true, this rule is an operator-set authoritative baseline for the tools it matches: it takes
/// precedence over generic tier/default rules in <em>both</em> directions (a specific Allow beats a
/// generic Ask, and a specific Ask beats a generic Allow), independent of the resolver's normal
/// Deny&gt;Ask&gt;Allow phase ordering. It is evaluated after safety gates and Deny rules, so a Deny
/// (including a bypass-immune <c>DeniedTools</c> rule) or a safety gate still wins over it. This is
/// how a plugin's <c>AutonomyLevel</c> scopes the autonomy of its own tools without editing the
/// global default tier. Ordinary rules leave this false and are unaffected.
/// </param>
public sealed record ToolPermissionRule(
    string ToolPattern,
    string? OperationPattern,
    PermissionBehaviorType Behavior,
    PermissionRuleSource Source,
    int Priority,
    bool IsBypassImmune = false,
    bool IsAuthoritativeBaseline = false);
