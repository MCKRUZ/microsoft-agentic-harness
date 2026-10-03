using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<AgentConversationCache> _logger;
    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(30);

    private static string ContextCacheKey(string conversationId) => $"{conversationId}::context";

    /// <summary>A cached agent together with the build inputs it was made from.</summary>
    private sealed record CachedAgent(AIAgent Agent, AgentBuildFingerprint Fingerprint);

    public AgentConversationCache(
        IMemoryCache cache,
        IAgentFactory agentFactory,
        IConversationRegistrationTracker registrationTracker,
        ISkillCompletionTracker completionTracker,
        ILogger<AgentConversationCache> logger)
    {
        _cache = cache;
        _agentFactory = agentFactory;
        _registrationTracker = registrationTracker;
        _completionTracker = completionTracker;
        _logger = logger;
    }

    public async Task<AIAgent> GetOrCreateAsync(
        string conversationId,
        IReadOnlyList<string> skillIds,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = AgentBuildFingerprint.From(skillIds, options);

        if (_cache.TryGetValue(conversationId, out CachedAgent? cached) && cached is not null)
        {
            if (cached.Fingerprint.Equals(fingerprint))
                return cached.Agent;

            // The turn asks for something other than what this agent was built for — a per-run
            // deployment override, a changed manifest. Nothing wrote settings, so nobody evicted;
            // fall through and build the requested one. The old entry is deliberately NOT dropped
            // first: the Set calls below replace it (finalising the old trace writer on the way), so a
            // failed build — a bad deployment name, say — leaves the working agent in place rather than
            // the conversation with none. Never Evict here: the conversation carries on, so its
            // unlocked skills and registration history stay.
            _logger.LogDebug(
                "Rebuilding agent for conversation {ConversationId}: build inputs changed since it was cached",
                conversationId);
        }

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
        _cache.Set(conversationId, new CachedAgent(built.Agent, fingerprint), entryOptions);

        // The context carries this run's trace writer when execution tracing is on, and that writer
        // owns a file handle and a semaphore that must be released. Finalising it on the cache
        // entry's own eviction — rather than in Evict — is what makes the sliding-expiry path work
        // too: a conversation that simply goes idle never calls Evict, so hanging the cleanup off
        // the explicit call alone would leak every abandoned conversation and leave its manifest
        // permanently marked incomplete.
        var contextEntryOptions = new MemoryCacheEntryOptions { SlidingExpiration = SlidingExpiration }
            .RegisterPostEvictionCallback(
                static (_, value, _, state) => CompleteTraceWriter(value, state as ILogger),
                _logger);
        _cache.Set(ContextCacheKey(conversationId), built.Context, contextEntryOptions);

        return built.Agent;
    }

    public AgentExecutionContext? TryGetContext(string conversationId)
        => _cache.TryGetValue(ContextCacheKey(conversationId), out AgentExecutionContext? ctx) ? ctx : null;

    public void Invalidate(string conversationId)
    {
        _cache.Remove(conversationId);
        _cache.Remove(ContextCacheKey(conversationId));
    }

    public void Evict(string conversationId)
    {
        Invalidate(conversationId);
        _registrationTracker.Evict(conversationId);
        // Clear skill-prerequisite completion state keyed by this conversation so a re-created
        // conversation reusing the same id starts with no unlocked skills and no leaked entries.
        _completionTracker.ClearConversation(conversationId);
    }

    /// <summary>
    /// Finalizes and disposes the execution-trace writer an evicted context was carrying, if any —
    /// delegates to <see cref="Traces.ExecutionTraceWriterCleanup.CompleteAsync"/>, the logic shared
    /// with <c>MagenticAgentTurnRunner</c>'s equivalent, non-cache-eviction-driven cleanup.
    /// </summary>
    /// <remarks>
    /// Blocking here does not block a caller: this callback is observed to run asynchronously, off
    /// the thread that triggered the eviction. That is not asserted from documentation — the first
    /// draft of <c>AgentConversationCacheTraceLifecycleTests</c> asserted immediately after
    /// <c>Evict</c> and <c>Remove</c> and failed, which is the measurement. Those tests now wait on
    /// a signal rather than racing it, and would fail loudly if the dispatch ever became
    /// synchronous, since the signal would already be set. The work itself is a short atomic file
    /// write plus a handle close.
    /// </remarks>
    private static void CompleteTraceWriter(object? evictedContext, ILogger? logger)
    {
        if (evictedContext is not AgentExecutionContext context)
            return;

        Traces.ExecutionTraceWriterCleanup.CompleteAsync(context, logger, CancellationToken.None)
            .GetAwaiter().GetResult();
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
