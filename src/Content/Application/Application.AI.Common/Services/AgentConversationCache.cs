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
    /// Finalizes and disposes the execution-trace writer an evicted context was carrying, if any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Interfaces.Traces.ITraceWriter.CompleteAsync"/> stamps <c>write_completed</c> into
    /// the run manifest; without it every production run stays flagged incomplete to any reader that
    /// honours the flag, and the writer's semaphore and file handle are never released. Only the
    /// meta-harness evaluation loop did this correctly before #505 — the conversation path created
    /// writers and abandoned them, which was harmless only for as long as nothing wrote to them.
    /// </para>
    /// <para>
    /// Blocking here does not block a caller: this callback is observed to run asynchronously, off
    /// the thread that triggered the eviction. That is not asserted from documentation — the first
    /// draft of <c>AgentConversationCacheTraceLifecycleTests</c> asserted immediately after
    /// <c>Evict</c> and <c>Remove</c> and failed, which is the measurement. Those tests now wait on
    /// a signal rather than racing it, and would fail loudly if the dispatch ever became
    /// synchronous, since the signal would already be set. The work itself is a short atomic file
    /// write plus a handle close.
    ///
    /// Failure is contained rather than propagated, and logged rather than hidden: an eviction
    /// callback that throws surfaces on an unrelated thread and would take out whatever triggered
    /// the eviction, and losing a manifest stamp must not be able to fail a conversation.
    /// </para>
    /// </remarks>
    private static void CompleteTraceWriter(object? evictedContext, ILogger? logger)
    {
        if (evictedContext is not AgentExecutionContext context
            || context.AdditionalProperties is null
            || !context.AdditionalProperties.TryGetValue(
                Interfaces.Traces.ITraceWriter.AdditionalPropertiesKey, out var stashed)
            || stashed is not Interfaces.Traces.ITraceWriter writer)
        {
            return;
        }

        try
        {
            writer.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Swallowed, but never silently: this repo's rule is that an error may be tolerated,
            // not hidden. A run whose manifest was never stamped is indistinguishable on disk from
            // one still in flight, so without this line the only evidence would be absence.
            logger?.LogWarning(ex,
                "Failed to finalize the execution trace for run {ExecutionRunId} — its manifest "
                + "will stay marked incomplete.",
                writer.Scope.ExecutionRunId);
        }
        finally
        {
            try
            {
                writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Failed to dispose the execution trace writer for run {ExecutionRunId} — its "
                    + "file handle and semaphore may not have been released.",
                    writer.Scope.ExecutionRunId);
            }
        }
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
