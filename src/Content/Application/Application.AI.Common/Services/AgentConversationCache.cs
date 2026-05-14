using Application.AI.Common.Interfaces;
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
    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(30);

    public AgentConversationCache(IMemoryCache cache, IAgentFactory agentFactory)
    {
        _cache = cache;
        _agentFactory = agentFactory;
    }

    public async Task<AIAgent> GetOrCreateAsync(
        string conversationId,
        string skillId,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(conversationId, out AIAgent? cached) && cached is not null)
            return cached;

        var agent = await _agentFactory.CreateAgentFromSkillAsync(skillId, options, cancellationToken);

        _cache.Set(conversationId, agent, new MemoryCacheEntryOptions
        {
            SlidingExpiration = SlidingExpiration
        });

        return agent;
    }

    public void Evict(string conversationId) => _cache.Remove(conversationId);
}
