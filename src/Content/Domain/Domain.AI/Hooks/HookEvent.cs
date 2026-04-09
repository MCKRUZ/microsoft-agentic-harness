namespace Domain.AI.Hooks;

/// <summary>
/// Defines the lifecycle events that hooks can subscribe to.
/// Events cover tool execution, session management, compaction, and agent delegation.
/// </summary>
public enum HookEvent
{
    /// <summary>Fires before a tool is executed. Hooks can modify input or block execution.</summary>
    PreToolUse,

    /// <summary>Fires after a tool completes successfully. Hooks can modify output.</summary>
    PostToolUse,

    /// <summary>Fires when a session starts.</summary>
    SessionStart,

    /// <summary>Fires when a session ends.</summary>
    SessionEnd,

    /// <summary>Fires before context compaction begins.</summary>
    PreCompact,

    /// <summary>Fires after context compaction completes.</summary>
    PostCompact,

    /// <summary>Fires when a subagent is spawned.</summary>
    SubagentStart,

    /// <summary>Fires when a subagent completes or is terminated.</summary>
    SubagentStop,

    /// <summary>Fires when a skill is loaded into the agent context.</summary>
    SkillLoaded,

    /// <summary>Fires when a skill is removed from the agent context.</summary>
    SkillUnloaded,

    /// <summary>Fires when a new tool is registered at runtime.</summary>
    ToolRegistered,

    /// <summary>Fires when a tool is unregistered at runtime.</summary>
    ToolUnregistered,

    /// <summary>Fires before an agent turn begins processing.</summary>
    PreTurn,

    /// <summary>Fires after an agent turn completes.</summary>
    PostTurn,

    /// <summary>Fires when context budget reaches the warning threshold.</summary>
    BudgetWarning,

    /// <summary>Fires when context budget is exceeded.</summary>
    BudgetExceeded
}
