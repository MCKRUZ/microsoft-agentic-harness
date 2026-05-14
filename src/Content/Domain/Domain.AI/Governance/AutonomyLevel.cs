namespace Domain.AI.Governance;

/// <summary>
/// Defines the trust tier assigned to an agent instance, controlling its baseline
/// permission behavior. Higher numeric values represent more trust.
/// </summary>
/// <remarks>
/// <para>
/// Tiers are orthogonal to <see cref="Agents.SubagentType"/>. An Explore agent could be
/// Restricted (read-only browsing) or Autonomous (full filesystem access). The tier is
/// per-instance, set when the subagent is defined or the supervisor creates a delegation.
/// </para>
/// <para>
/// The numeric ordering enables <c>&gt;=</c> comparisons for "requires at least tier X" checks.
/// </para>
/// </remarks>
public enum AutonomyLevel
{
    /// <summary>
    /// Read-only tier. Default permission behavior is Ask, forcing approval for every action.
    /// Safety gates handle true Deny scenarios.
    /// </summary>
    Restricted = 0,

    /// <summary>
    /// Recommend-and-wait tier. Default behavior is also Ask, but Supervised agents can have
    /// specific tool Allow overrides via <c>ToolOverrides</c> in tier policy configuration.
    /// </summary>
    Supervised = 1,

    /// <summary>
    /// Act-within-guardrails tier. Default behavior is Allow. Safety gates and AGT policies
    /// still apply as a ceiling above the tier's baseline.
    /// </summary>
    Autonomous = 2
}
