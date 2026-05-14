using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Resilience;
using Domain.AI.Resilience;
using Domain.Common.Config.AI.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace Infrastructure.AI.Resilience;

/// <summary>
/// Composes a <see cref="ResilientChatClient"/> from the configured fallback chain.
/// Reads provider entries from <see cref="ResilienceConfig.FallbackChain"/>, creates
/// raw <see cref="IChatClient"/> instances via <see cref="IChatClientFactory"/>,
/// wraps each in a per-provider Polly resilience pipeline, and caches the result.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="ResilienceConfig.Enabled"/> is false, returns the primary provider's
/// raw client directly — no Polly wrapping, no fallback chain, no overhead.
/// </para>
/// <para>
/// The composed client is cached (lazy-initialized). The fallback chain is static for
/// the process lifetime. Config changes require restart.
/// </para>
/// </remarks>
public sealed class ResilientChatClientProvider : IResilientChatClientProvider
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IOptionsMonitor<ResilienceConfig> _resilienceConfig;
    private readonly PollyProviderHealthMonitor _healthMonitor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ResilientChatClientProvider> _logger;
    private readonly Lazy<Task<IChatClient>> _cachedClient;

    /// <summary>Creates a new resilient chat client provider.</summary>
    /// <param name="chatClientFactory">Factory for creating raw per-provider clients.</param>
    /// <param name="resilienceConfig">Resilience configuration from Options pattern.</param>
    /// <param name="healthMonitor">Concrete health monitor for <see cref="PollyProviderHealthMonitor.ReportStateChange"/> wiring.</param>
    /// <param name="loggerFactory">Logger factory for creating typed loggers for composed clients.</param>
    /// <param name="logger">Logger for chain composition events.</param>
    public ResilientChatClientProvider(
        IChatClientFactory chatClientFactory,
        IOptionsMonitor<ResilienceConfig> resilienceConfig,
        PollyProviderHealthMonitor healthMonitor,
        ILoggerFactory loggerFactory,
        ILogger<ResilientChatClientProvider> logger)
    {
        _chatClientFactory = chatClientFactory;
        _resilienceConfig = resilienceConfig;
        _healthMonitor = healthMonitor;
        _loggerFactory = loggerFactory;
        _logger = logger;
        // PublicationOnly: don't cache faulted tasks permanently — allow retry on transient factory errors
        _cachedClient = new Lazy<Task<IChatClient>>(ComposeChainAsync, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <inheritdoc/>
    public Task<IChatClient> GetResilientChatClientAsync(CancellationToken ct = default)
    {
        return _cachedClient.Value;
    }

    private async Task<IChatClient> ComposeChainAsync()
    {
        var config = _resilienceConfig.CurrentValue;
        var chain = config.FallbackChain;

        if (chain.Length == 0)
            throw new InvalidOperationException(
                "ResilienceConfig.FallbackChain is empty. Configure at least one provider.");

        if (!config.Enabled)
        {
            var primary = chain[0];
            _logger.LogWarning("Resilience disabled — returning raw client for {DeploymentId}", primary.DeploymentId);
            return await _chatClientFactory.GetChatClientAsync(
                primary.ClientType, primary.DeploymentId);
        }

        var providers = new List<ResilientChatClient.ProviderEntry>(chain.Length);

        foreach (var entry in chain)
        {
            var rawClient = await _chatClientFactory.GetChatClientAsync(
                entry.ClientType, entry.DeploymentId);

            var pipeline = ProviderResiliencePipelineBuilder.Build(
                providerName: entry.DeploymentId,
                config: config,
                out var stateProvider,
                onCircuitStateChanged: newState =>
                    _healthMonitor.ReportStateChange(entry.DeploymentId, newState),
                logger: _logger);

            // Polly v8 CircuitBreakerStateProvider can only bind to one circuit breaker strategy,
            // so the stream pipeline needs its own instance.
            var streamStateProvider = new CircuitBreakerStateProvider();
            var streamPipeline = ProviderResiliencePipelineBuilder.BuildForStreamInitiation(
                providerName: entry.DeploymentId,
                config: config,
                sharedStateProvider: streamStateProvider,
                onCircuitStateChanged: newState =>
                    _healthMonitor.ReportStateChange(entry.DeploymentId, newState),
                logger: _logger);

            providers.Add(new ResilientChatClient.ProviderEntry(
                entry.DeploymentId, rawClient, pipeline, streamPipeline));

            _logger.LogDebug("Created provider entry {DeploymentId} ({ClientType})",
                entry.DeploymentId, entry.ClientType);
        }

        var providerNames = string.Join(", ", chain.Select(c => c.DeploymentId));
        _logger.LogInformation("Composed resilient chat client with {Count} providers: {ProviderNames}",
            providers.Count, providerNames);

        return new ResilientChatClient(providers, _healthMonitor,
            _loggerFactory.CreateLogger<ResilientChatClient>());
    }
}
