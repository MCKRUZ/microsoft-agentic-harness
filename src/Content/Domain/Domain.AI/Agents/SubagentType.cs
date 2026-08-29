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
    General,

    /// <summary>
    /// Not a built-in profile. Marks a delegation that targeted a specific, named
    /// <c>AGENT.md</c>-registered peer agent by id (#518) — <c>AgentCandidate.AgentId</c> /
    /// <c>DelegationRecord.DelegateAgentId</c> carry that id, and the delegation bypassed
    /// <c>ISupervisorStrategy</c>'s scoring entirely (the caller already named its target).
    /// <c>ISubagentProfileRegistry.GetProfile</c> has no entry for this value — a candidate or record
    /// carrying it is built and run through <c>ISupervisor</c> directly, never resolved from that
    /// registry.
    /// </summary>
    NamedAgent
}
