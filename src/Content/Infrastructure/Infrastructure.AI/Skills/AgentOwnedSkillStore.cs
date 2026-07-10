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
}
