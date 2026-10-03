using Domain.AI.Agents;
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
    /// a new one on a miss using the supplied <paramref name="skillIds"/> and <paramref name="options"/>.
    /// Multiple skill IDs are merged into a single agent execution context.
    /// </summary>
    Task<AIAgent> GetOrCreateAsync(
        string conversationId,
        IReadOnlyList<string> skillIds,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the <see cref="AgentExecutionContext"/> that was used to build the cached
    /// agent for <paramref name="conversationId"/>, or <c>null</c> when the conversation
    /// has no live agent. Used by per-turn observability code (context snapshots) that
    /// needs to inspect the agent's system prompt, skill list, tools, and MCP attribution
    /// without rebuilding the context.
    /// </summary>
    AgentExecutionContext? TryGetContext(string conversationId);

    /// <summary>
    /// Removes the agent for <paramref name="conversationId"/> from the cache.
    /// Call when the conversation ends to release the agent promptly.
    /// </summary>
    void Evict(string conversationId);

    /// <summary>
    /// Drops the built agent and its context so the next turn rebuilds them from current inputs,
    /// while the conversation carries on: its skill-prerequisite progress and registration history
    /// are kept.
    /// </summary>
    /// <remarks>
    /// For a change that alters what the agent is built from but not what the conversation has done —
    /// its settings changing, for example. <see cref="Evict"/> is the end-of-conversation form: it also
    /// forgets which skills the conversation has unlocked, so using it for a mid-conversation rebuild
    /// would lock them all again. The dropped context's execution trace is finalised as usual and the
    /// rebuilt agent starts a new one.
    /// </remarks>
    void Invalidate(string conversationId);
}
