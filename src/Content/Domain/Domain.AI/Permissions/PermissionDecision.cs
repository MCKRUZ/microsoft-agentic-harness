namespace Domain.AI.Permissions;

/// <summary>
/// The result of resolving permissions for a specific tool invocation.
/// Contains the decision, reason, and the rule that matched (if any).
/// </summary>
/// <param name="Behavior">The resolved permission behavior.</param>
/// <param name="Reason">Human-readable explanation of why this decision was made.</param>
/// <param name="MatchedRule">The rule that produced this decision, or null if no rule matched (default behavior applied).</param>
/// <param name="Source">The source of the matched rule, or null if default.</param>
public sealed record PermissionDecision(
    PermissionBehaviorType Behavior,
    string Reason,
    ToolPermissionRule? MatchedRule = null,
    PermissionRuleSource? Source = null)
{
    /// <summary>Creates an Allow decision.</summary>
    /// <param name="reason">Human-readable explanation of the decision.</param>
    /// <param name="rule">The rule that produced this decision, if any.</param>
    /// <returns>A new <see cref="PermissionDecision"/> with <see cref="PermissionBehaviorType.Allow"/>.</returns>
    public static PermissionDecision Allow(string reason, ToolPermissionRule? rule = null) =>
        new(PermissionBehaviorType.Allow, reason, rule, rule?.Source);

    /// <summary>Creates a Deny decision.</summary>
    /// <param name="reason">Human-readable explanation of the decision.</param>
    /// <param name="rule">The rule that produced this decision, if any.</param>
    /// <returns>A new <see cref="PermissionDecision"/> with <see cref="PermissionBehaviorType.Deny"/>.</returns>
    public static PermissionDecision Deny(string reason, ToolPermissionRule? rule = null) =>
        new(PermissionBehaviorType.Deny, reason, rule, rule?.Source);

    /// <summary>Creates an Ask decision.</summary>
    /// <param name="reason">Human-readable explanation of the decision.</param>
    /// <param name="rule">The rule that produced this decision, if any.</param>
    /// <returns>A new <see cref="PermissionDecision"/> with <see cref="PermissionBehaviorType.Ask"/>.</returns>
    public static PermissionDecision Ask(string reason, ToolPermissionRule? rule = null) =>
        new(PermissionBehaviorType.Ask, reason, rule, rule?.Source);
}
