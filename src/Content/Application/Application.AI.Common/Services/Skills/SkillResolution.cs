using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Skills;

namespace Application.AI.Common.Services.Skills;

/// <summary>
/// The one rule for turning a skill id into the definition an agent is built from: the owning agent's own
/// nested skill first, the global registry second.
/// </summary>
/// <remarks>
/// Shared by <c>AgentFactory</c>, which builds from the result, and <c>AgentConversationCache</c>, which
/// fingerprints it. Two copies of the rule would let the cache watch one definition while the factory
/// built from another, and a cached agent would then go stale with nothing to say so.
/// </remarks>
internal static class SkillResolution
{
    /// <summary>
    /// Resolves <paramref name="skillId"/>, or returns null when neither source knows it.
    /// </summary>
    /// <param name="skillId">The skill id to resolve.</param>
    /// <param name="owningAgentId">The agent whose own skills shadow the global pool, when there is one.</param>
    /// <param name="ownedSkills">The owned-skill store; null in hosts that do not discover agents.</param>
    /// <param name="registry">The global skill registry.</param>
    public static SkillDefinition? Resolve(
        string skillId,
        string? owningAgentId,
        IAgentOwnedSkillStore? ownedSkills,
        ISkillMetadataRegistry registry)
    {
        if (owningAgentId is not null && ownedSkills is not null)
        {
            var owned = ownedSkills.TryGet(owningAgentId, skillId);
            if (owned is not null)
                return owned;
        }

        return registry.TryGet(skillId);
    }
}
