using System.Collections.Concurrent;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Evaluation.Judges;

/// <summary>
/// Default <see cref="IJudgeChatClientProvider"/> backed by <see cref="IChatClientFactory"/>.
/// Resolves and caches one <see cref="IChatClient"/> per (ClientType, Deployment) via
/// single-flight construction so a large suite shares one client and concurrent
/// first-callers don't each build (and then discard) their own.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately does NOT consult <c>IModelRouter</c> — see
/// <see cref="IJudgeChatClientProvider"/> for the reproducibility rationale.
/// </para>
/// <para>
/// Uses <see cref="Lazy{T}"/> over <see cref="Task{TResult}"/> to coordinate concurrent
/// first-callers: all racers await the same in-flight task, only one
/// <see cref="IChatClient"/> is constructed, and no orphaned clients leak handler
/// pipelines or auth-token refresh state.
/// </para>
/// </remarks>
public sealed class DefaultJudgeChatClientProvider : IJudgeChatClientProvider, IDisposable
{
    private readonly IChatClientFactory _factory;
    private readonly IOptionsMonitor<JudgeOptions> _options;
    private readonly ConcurrentDictionary<(AIAgentFrameworkClientType, string), Lazy<Task<IChatClient>>> _cache = new();
    private readonly IDisposable? _optionsChangeSubscription;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultJudgeChatClientProvider"/> class.
    /// </summary>
    public DefaultJudgeChatClientProvider(
        IChatClientFactory factory,
        IOptionsMonitor<JudgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        _factory = factory;
        _options = options;

        // When config reloads change the judge model, drain stale entries so the next
        // GetJudgeAsync rebuilds against the new options. Without this hook, the old
        // cached IChatClient lingers undisposed until process restart — handler
        // pipelines and auth-token refresh state accumulate across reloads.
        _optionsChangeSubscription = _options.OnChange((_, _) => EvictAndDispose());
    }

    /// <summary>
    /// Disposes the options-change subscription and all cached chat clients.
    /// </summary>
    public void Dispose()
    {
        _optionsChangeSubscription?.Dispose();
        EvictAndDispose();
    }

    private void EvictAndDispose()
    {
        foreach (var key in _cache.Keys.ToList())
        {
            if (_cache.TryRemove(key, out var lazy) && lazy.IsValueCreated)
            {
                try
                {
                    if (lazy.Value.Status == TaskStatus.RanToCompletion && lazy.Value.Result is IDisposable d)
                    {
                        d.Dispose();
                    }
                }
                catch { /* best-effort eviction */ }
            }
        }
    }

    /// <inheritdoc />
    public Task<IChatClient> GetJudgeAsync(CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(opts.Deployment))
        {
            throw new InvalidOperationException(
                "JudgeOptions.Deployment must be configured before invoking an LLM judge. " +
                "Wire it via services.Configure<JudgeOptions>(o => o.Deployment = \"...\")");
        }

        var clientType = opts.ClientType ?? PickFirstAvailable();
        return ResolveAsync(clientType, opts.Deployment, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IChatClient> GetJudgeAsync(
        AIAgentFrameworkClientType clientType,
        string deployment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidOperationException(
                "A judge panelist deployment must be non-empty. Set it on the panelist spec " +
                "or leave the panelist's model fields null to fall back to JudgeOptions.");
        }

        // Explicit panelist model — no PickFirstAvailable fallback; the caller chose it.
        return ResolveAsync(clientType, deployment, cancellationToken);
    }

    private async Task<IChatClient> ResolveAsync(
        AIAgentFrameworkClientType clientType,
        string deployment,
        CancellationToken cancellationToken)
    {
        var key = (clientType, deployment);

        // Single-flight: GetOrAdd returns the same Lazy<Task<IChatClient>> to every
        // concurrent caller for a given key. The factory delegate inside the Lazy runs
        // at most once, so only one IChatClient is ever constructed per key — losers
        // await the winner's task instead of each building (and discarding) their own.
        // The default judge and any panelist requesting the same model share one client.
        var lazy = _cache.GetOrAdd(
            key,
            k => new Lazy<Task<IChatClient>>(
                () => _factory.GetChatClientAsync(k.Item1, k.Item2, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller-side cancellation — leave the Lazy intact; underlying construction
            // may still succeed for the next non-cancelled caller.
            throw;
        }
        catch
        {
            // Construction failure (transient infra blip, auth flake, etc.). Without
            // eviction, the faulted Lazy would be cached forever and every subsequent
            // call would replay the same exception until process restart. Drop the
            // failed entry so the next caller triggers a fresh single-flight attempt.
            _cache.TryRemove(
                new KeyValuePair<(AIAgentFrameworkClientType, string), Lazy<Task<IChatClient>>>(key, lazy));
            throw;
        }
    }

    private AIAgentFrameworkClientType PickFirstAvailable()
    {
        foreach (var (type, available) in _factory.GetAvailableProviders())
        {
            if (available) return type;
        }
        throw new InvalidOperationException(
            "No AI provider is available for the LLM judge. " +
            "Ensure at least one of AzureOpenAI / OpenAI / PersistentAgents is configured.");
    }
}
