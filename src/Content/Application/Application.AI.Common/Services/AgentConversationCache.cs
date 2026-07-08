using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Memory;

namespace Application.AI.Common.Services;

/// <summary>
/// <see cref="IMemoryCache"/>-backed implementation of <see cref="IAgentConversationCache"/>.
/// Agents are evicted explicitly on conversation end or automatically after 30 minutes of inactivity.
/// </summary>
internal sealed class AgentConversationCache : IAgentConversationCache
{
    private readonly IMemoryCache _cache;
    private readonly IAgentFactory _agentFactory;
    private readonly IConversationRegistrationTracker _registrationTracker;
    private readonly ISkillCompletionTracker _completionTracker;
    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(30);

    private static string ContextCacheKey(string conversationId) => $"{conversationId}::context";

    public AgentConversationCache(
        IMemoryCache cache,
        IAgentFactory agentFactory,
        IConversationRegistrationTracker registrationTracker,
        ISkillCompletionTracker completionTracker)
    {
        _cache = cache;
        _agentFactory = agentFactory;
        _registrationTracker = registrationTracker;
        _completionTracker = completionTracker;
    }

    public async Task<AIAgent> GetOrCreateAsync(
        string conversationId,
        IReadOnlyList<string> skillIds,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(conversationId, out AIAgent? cached) && cached is not null)
            return cached;

        // Flow the conversation id into the agent build so the skill-prerequisite middleware
        // can scope completion tracking to this conversation. The factory reads it from
        // SkillAgentOptions.AdditionalProperties[AgentFactory.ConversationIdPropertyKey] and
        // throws when it is absent, so a skill declaring prerequisites would otherwise crash
        // every turn on the live path. A scope-bearing copy is used so the caller's options
        // instance is never mutated and cannot be cross-contaminated across conversations.
        var scopedOptions = WithConversationScope(options, conversationId);

        var built = await _agentFactory.CreateAgentWithContextFromSkillsAsync(
            skillIds, scopedOptions, cancellationToken);

        var entryOptions = new MemoryCacheEntryOptions { SlidingExpiration = SlidingExpiration };
        _cache.Set(conversationId, built.Agent, entryOptions);
        _cache.Set(ContextCacheKey(conversationId), built.Context, entryOptions);

        return built.Agent;
    }

    public AgentExecutionContext? TryGetContext(string conversationId)
        => _cache.TryGetValue(ContextCacheKey(conversationId), out AgentExecutionContext? ctx) ? ctx : null;

    public void Evict(string conversationId)
    {
        _cache.Remove(conversationId);
        _cache.Remove(ContextCacheKey(conversationId));
        _registrationTracker.Evict(conversationId);
        // Clear skill-prerequisite completion state keyed by this conversation so a re-created
        // conversation reusing the same id starts with no unlocked skills and no leaked entries.
        _completionTracker.ClearConversation(conversationId);
    }

    /// <summary>
    /// Returns a copy of <paramref name="options"/> carrying <paramref name="conversationId"/>
    /// under <see cref="AgentFactory.ConversationIdPropertyKey"/> in its additional properties,
    /// without mutating the caller-supplied instance or its dictionary.
    /// </summary>
    private static SkillAgentOptions WithConversationScope(SkillAgentOptions options, string conversationId)
    {
        var scopedProperties = options.AdditionalProperties is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(options.AdditionalProperties);
        scopedProperties[AgentFactory.ConversationIdPropertyKey] = conversationId;

        return options with { AdditionalProperties = scopedProperties };
    }
}
