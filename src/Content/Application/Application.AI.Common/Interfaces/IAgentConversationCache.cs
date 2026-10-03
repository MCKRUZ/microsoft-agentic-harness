using Domain.AI.Agents;
using Domain.AI.Skills;
using Microsoft.Agents.AI;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Keeps a live <see cref="AIAgent"/> alive for the duration of a conversation,
/// eliminating per-turn agent reconstruction overhead.
/// </summary>
/// <remarks>
/// The agent is created on the first turn (cache miss) and reused for later turns in the same
/// conversation <em>as long as each turn still asks for what it was built for</em>: a turn whose
/// skills or build-affecting options differ (a per-run deployment override, a changed agent
/// manifest) rebuilds the agent rather than being served the stale one. Explicit eviction via
/// <see cref="Evict"/> should be called when the conversation ends; a 30-minute sliding TTL handles
/// abandoned sessions.
/// </remarks>
public interface IAgentConversationCache
{
    /// <summary>
    /// Returns the cached agent for <paramref name="conversationId"/> when it was built from the same
    /// <paramref name="skillIds"/> and <paramref name="options"/>; otherwise builds and caches a new
    /// one, replacing any stale entry without forgetting the conversation's skill-completion state.
    /// Multiple skill IDs are merged into a single agent execution context.
    /// <see cref="SkillAgentOptions.AdditionalTools"/>, <see cref="SkillAgentOptions.MiddlewareTypes"/>,
    /// <see cref="SkillAgentOptions.AdditionalProperties"/> and <see cref="SkillAgentOptions.TraceScope"/>
    /// are not compared, so a hit does not honour a change to them.
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
