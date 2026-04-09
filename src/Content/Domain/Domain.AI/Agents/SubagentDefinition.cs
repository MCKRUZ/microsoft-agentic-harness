using Domain.AI.Permissions;

namespace Domain.AI.Agents;

/// <summary>
/// Defines the configuration for a subagent — its tool access, turn limits,
/// permission mode, and behavioral constraints.
/// </summary>
public sealed record SubagentDefinition
{
    /// <summary>The built-in agent type profile.</summary>
    public required SubagentType AgentType { get; init; }

    /// <summary>
    /// Explicit tool allowlist. Only these tools will be available to the subagent.
    /// Null means inherit all parent tools (filtered by denylist).
    /// </summary>
    public IReadOnlyList<string>? ToolAllowlist { get; init; }

    /// <summary>
    /// Explicit tool denylist. These tools will be removed from the subagent's tool pool.
    /// Applied after allowlist filtering.
    /// </summary>
    public IReadOnlyList<string>? ToolDenylist { get; init; }

    /// <summary>
    /// How the subagent handles permission prompts.
    /// Ask = bubble to user, Allow = auto-approve, Deny = auto-deny.
    /// </summary>
    public PermissionBehaviorType PermissionMode { get; init; } = PermissionBehaviorType.Ask;

    /// <summary>Maximum turns this subagent can execute before being terminated.</summary>
    public int MaxTurns { get; init; } = 10;

    /// <summary>Optional model override (e.g., use a cheaper model for exploration).</summary>
    public string? ModelOverride { get; init; }

    /// <summary>Optional system prompt override. Null uses the skill's default.</summary>
    public string? SystemPromptOverride { get; init; }

    /// <summary>Whether to inherit the parent's tool pool as the starting point.</summary>
    public bool InheritParentTools { get; init; } = true;
}
