using Domain.AI.Skills;
using Microsoft.Agents.AI;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Keeps a live <see cref="AIAgent"/> alive for the duration of a conversation,
/// eliminating per-turn agent reconstruction overhead.
/// </summary>
/// <remarks>
/// The agent is created on the first turn (cache miss) and reused for all subsequent
/// turns in the same conversation. Explicit eviction via <see cref="Evict"/> should be
/// called when the conversation ends; a 30-minute sliding TTL handles abandoned sessions.
/// </remarks>
public interface IAgentConversationCache
{
    /// <summary>
    /// Returns the cached agent for <paramref name="conversationId"/>, creating and caching
    /// a new one on a miss using the supplied <paramref name="skillId"/> and <paramref name="options"/>.
    /// </summary>
    Task<AIAgent> GetOrCreateAsync(
        string conversationId,
        string skillId,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the agent for <paramref name="conversationId"/> from the cache.
    /// Call when the conversation ends to release the agent promptly.
    /// </summary>
    void Evict(string conversationId);
}
