using Domain.AI.Agents;
using Domain.AI.Skills;

namespace Domain.AI.Bundles;

/// <summary>
/// The agent and owned skills that are active for a single bundle run, layered over the host's
/// startup-discovered registries for the duration of one async flow. A bundle's agent is never added to
/// the host's persistent agent registry and its skills are never added to the global skill pool; instead
/// this overlay is consulted <em>first</em> by the overlay-aware registries so the ephemeral definitions
/// are resolvable only within the run that set it, and vanish the moment that flow completes.
/// </summary>
/// <remarks>
/// This is the resolution-time projection of a <see cref="StagedBundle"/> — just the pieces the agent
/// factory needs to resolve and build the agent (its definition and its owned skills), without the
/// staging-only details. It is a pure value; the ambient plumbing that publishes it for a run lives in
/// the accessor that carries it, mirroring the host's existing per-turn <c>AsyncLocal</c> accessors.
/// </remarks>
public sealed record EphemeralAgentOverlay
{
    /// <summary>The ephemeral agent for this run, parsed from the bundle's <c>AGENT.md</c>.</summary>
    public required AgentDefinition Agent { get; init; }

    /// <summary>
    /// The skills owned by <see cref="Agent"/>, resolved owned-first (and only) for this run's agent id.
    /// Empty when the bundle declares no nested skills.
    /// </summary>
    public IReadOnlyList<SkillDefinition> OwnedSkills { get; init; } = [];

    /// <summary>
    /// Whether this overlay is the one for the agent identified by <paramref name="agentId"/> — the single
    /// authoritative definition of "the overlay owns this agent", so every resolution seam (agent metadata,
    /// owned skills) applies the overlay by the same rule and cannot drift into a half-applied state.
    /// Matching is case-insensitive, mirroring the host registries.
    /// </summary>
    /// <param name="agentId">The agent id being resolved.</param>
    public bool OwnsAgent(string agentId) =>
        string.Equals(Agent.Id, agentId, StringComparison.OrdinalIgnoreCase);
}
