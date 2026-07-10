using Domain.AI.Skills;

namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Holds skills that are <em>owned</em> by a specific agent — the <c>SKILL.md</c> files discovered
/// under an agent's own <c>&lt;agentDir&gt;/skills/</c> directory. These are private to their owning
/// agent: this store resolves them only for that agent, and discovery writes them here rather than into
/// the global <see cref="ISkillMetadataRegistry"/>, so one agent's nested skill can neither be seen by
/// another agent through this store nor collide with a shared skill of the same id in the global pool.
/// This isolation assumes the configured skill roots and agent roots are disjoint (the shipped default):
/// the global registry scans skill roots recursively and is agent-unaware, so pointing a skill root at
/// an ancestor of an agent directory would let it independently discover — and globally publish — that
/// agent's nested skills. Keep the roots disjoint.
/// </summary>
/// <remarks>
/// This is the storage half of the "a skill is a sub-level of its agent" model: agent discovery
/// (<c>AgentMetadataRegistry</c>) populates the store as it parses each <c>AGENT.md</c>, and agent
/// construction (<c>AgentFactory</c>) consults it — keyed by the owning agent id — <em>before</em>
/// falling back to the global registry. An implementation must be safe for concurrent reads during
/// agent construction and the (one-time, discovery-driven) writes that populate it.
/// </remarks>
public interface IAgentOwnedSkillStore
{
    /// <summary>
    /// Registers a skill as owned by the agent with id <paramref name="agentId"/>. Re-registering the
    /// same <c>(agentId, skill.Id)</c> pair overwrites the prior definition (idempotent re-discovery).
    /// </summary>
    /// <param name="agentId">The id of the owning agent.</param>
    /// <param name="skill">The discovered nested skill definition.</param>
    void Register(string agentId, SkillDefinition skill);

    /// <summary>
    /// Returns the skill with id <paramref name="skillId"/> owned by <paramref name="agentId"/>, or
    /// <see langword="null"/> when that agent owns no such skill. Agent id and skill id are matched
    /// case-insensitively, mirroring the global registry.
    /// </summary>
    /// <param name="agentId">The id of the owning agent.</param>
    /// <param name="skillId">The id of the skill to resolve.</param>
    SkillDefinition? TryGet(string agentId, string skillId);

    /// <summary>
    /// Returns every skill owned by <paramref name="agentId"/>, or an empty list when the agent owns
    /// none.
    /// </summary>
    /// <param name="agentId">The id of the owning agent.</param>
    IReadOnlyList<SkillDefinition> GetForAgent(string agentId);
}
