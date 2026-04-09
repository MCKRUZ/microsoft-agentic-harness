namespace Domain.AI.Agents;

/// <summary>
/// Built-in subagent type profiles. Each type has a predefined tool allowlist,
/// max turn limit, and behavioral constraints.
/// </summary>
public enum SubagentType
{
    /// <summary>Read-only exploration — can read files, search, and browse but not modify.</summary>
    Explore,

    /// <summary>Planning only — no tools, produces plans and analysis.</summary>
    Plan,

    /// <summary>Verification — read-only tools for testing and validation.</summary>
    Verify,

    /// <summary>Full execution — all tools available, highest turn budget.</summary>
    Execute,

    /// <summary>General purpose — balanced tool access, moderate turn budget.</summary>
    General
}
