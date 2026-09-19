using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Skills;

namespace Infrastructure.AI.Skills;

/// <summary>
/// In-memory, thread-safe <see cref="IAgentOwnedSkillStore"/>. Skills are held in a two-level map —
/// agent id → (skill id → definition) — both levels compared case-insensitively to match the global
/// registry. Registered as a singleton so the writer (agent discovery) and the readers (agent
/// construction) share one instance.
/// </summary>
public sealed class AgentOwnedSkillStore : IAgentOwnedSkillStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SkillDefinition>> _byAgent =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(string agentId, SkillDefinition skill)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(skill);

        var skills = _byAgent.GetOrAdd(agentId, _ => new(StringComparer.OrdinalIgnoreCase));
        skills[skill.Id] = skill;
    }

    /// <inheritdoc />
    public SkillDefinition? TryGet(string agentId, string skillId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(skillId))
            return null;

        return _byAgent.TryGetValue(agentId, out var skills) && skills.TryGetValue(skillId, out var skill)
            ? skill
            : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetForAgent(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return [];

        return _byAgent.TryGetValue(agentId, out var skills) ? skills.Values.ToList() : [];
    }

    /// <summary>
    /// Atomically replaces every skill owned by <paramref name="agentId"/> with
    /// <paramref name="skills"/>. Unlike <see cref="Register"/> (which only ever adds or overwrites
    /// one skill id), this drops any previously-registered skill for the agent that is not present
    /// in <paramref name="skills"/> — the operation a reload needs when a nested <c>SKILL.md</c> was
    /// deleted from disk, which <see cref="Register"/> alone can never observe (issue #705).
    /// </summary>
    /// <remarks>
    /// Not part of <see cref="Application.AI.Common.Interfaces.Skills.IAgentOwnedSkillStore"/> —
    /// deliberately on the concrete type only. Discovery-time writes are a host-level lifecycle
    /// operation, not something the per-bundle-run overlay decorator should participate in, and
    /// keeping this off the interface avoids touching it, the decorator, and the read-path test
    /// fakes that implement it.
    /// </remarks>
    /// <param name="agentId">The id of the owning agent.</param>
    /// <param name="skills">The complete, current set of skills owned by <paramref name="agentId"/>.</param>
    public void ReplaceAgentSkills(string agentId, IEnumerable<SkillDefinition> skills)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(skills);

        var replacement = new ConcurrentDictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
            replacement[skill.Id] = skill;

        if (replacement.IsEmpty)
        {
            // No entry to hold — behaviourally identical to a present-but-empty map for every
            // reader (TryGet/GetForAgent both already treat "no key" and "empty map" the same),
            // and avoids leaving an allocated entry for the common case of a skill-less agent.
            _byAgent.TryRemove(agentId, out _);
            return;
        }

        // Indexer assignment on ConcurrentDictionary is atomic per key: a concurrent reader sees
        // either the old map or the fully-built new one, never a partially-populated replacement.
        _byAgent[agentId] = replacement;
    }

    /// <summary>
    /// Removes every skill owned by <paramref name="agentId"/>. Used when an agent itself is
    /// removed on reload — without this, a deleted agent's nested skills would linger in the store
    /// forever, resolvable by an id no agent owns any more (issue #705).
    /// </summary>
    /// <remarks>Not on the interface, for the same reason as <see cref="ReplaceAgentSkills"/>.</remarks>
    /// <param name="agentId">The id of the agent to remove all owned skills for.</param>
    public void RemoveAgent(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return;

        _byAgent.TryRemove(agentId, out _);
    }
}
