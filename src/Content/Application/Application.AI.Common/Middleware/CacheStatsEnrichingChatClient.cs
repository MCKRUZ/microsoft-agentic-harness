using System.Diagnostics;
using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Middleware;

/// <summary>
/// Chat client middleware that records prompt-cache telemetry for providers whose cache token
/// counts are only available out-of-band (notably the OpenAI-compatible OpenRouter path).
/// </summary>
/// <remarks>
/// <para>
/// After each provider call it captures the response identifier and, on a background task, fetches
/// the generation's usage record via <see cref="IGenerationStatsClient"/> and emits
/// <see cref="LlmUsageMetrics.CacheReadTokens"/>, <see cref="LlmUsageMetrics.CacheHitRate"/>, and
/// <see cref="LlmUsageMetrics.CacheSavings"/>. These are the inputs the dashboard Cache Read /
/// Cache Efficiency / Cache Savings tiles were built for but never received on this path, because
/// the inline usage object does not carry the cache fields and the streamed body cannot be read.
/// </para>
/// <para>
/// The fetch runs off the response path (the stats record lags the call by a few seconds and the
/// client polls for it) so it never adds latency to the agent turn, and it fails soft — any error
/// is logged at debug and swallowed. The cost/cache-read counts emitted here are intentionally the
/// <em>only</em> source of cache metrics on this path; <c>LlmTokenTrackingProcessor</c> stays
/// silent on the cache instruments when the span carries no cache attributes, so there is no
/// double counting.
/// </para>
/// </remarks>
public sealed class CacheStatsEnrichingChatClient : DelegatingChatClient
{
    private readonly IGenerationStatsClient _statsClient;
    private readonly string _agentName;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheStatsEnrichingChatClient"/> class.
    /// </summary>
    /// <param name="innerClient">The inner chat client to wrap.</param>
    /// <param name="statsClient">Client used to fetch the out-of-band generation usage record.</param>
    /// <param name="agentName">Agent name used as the <c>agent.name</c> metric dimension.</param>
    /// <param name="logger">Logger for diagnostic (debug-level) failures.</param>
    public CacheStatsEnrichingChatClient(
        IChatClient innerClient,
        IGenerationStatsClient statsClient,
        string agentName,
        ILogger<CacheStatsEnrichingChatClient> logger)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(statsClient);
        ArgumentNullException.ThrowIfNull(logger);
        _statsClient = statsClient;
        _agentName = string.IsNullOrWhiteSpace(agentName) ? "unknown" : agentName;
        _logger = logger;
    }

    private volatile Task _enrichmentCompletion = Task.CompletedTask;

    /// <summary>
    /// The most recently started background enrichment task. Exposed for deterministic testing so
    /// the fire-and-forget fetch can be awaited; never awaited on the production response path.
    /// Backed by a <c>volatile</c> field so the assignment on the response path is published with a
    /// memory barrier (the inner client may serve concurrent turns).
    /// </summary>
    internal Task EnrichmentCompletion => _enrichmentCompletion;

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        EnrichInBackground(response.ResponseId, response.ModelId);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? responseId = null;
        string? modelId = null;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            responseId ??= update.ResponseId;
            modelId ??= update.ModelId;
            yield return update;
        }

        EnrichInBackground(responseId, modelId);
    }

    /// <summary>
    /// Starts a background fetch of the generation stats and emits the cache metrics. Returns
    /// immediately; failures are logged at debug and never propagate to the caller.
    /// </summary>
    private void EnrichInBackground(string? responseId, string? modelId)
    {
        if (string.IsNullOrEmpty(responseId))
            return;

        _enrichmentCompletion = Task.Run(async () =>
        {
            try
            {
                var stats = await _statsClient
                    .GetGenerationStatsAsync(responseId, CancellationToken.None)
                    .ConfigureAwait(false);

                if (stats is not null)
                    EmitMetrics(stats, modelId, _agentName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Failed to enrich cache telemetry for generation {ResponseId}.", responseId);
            }
        });
    }

    /// <summary>
    /// Emits the cache metrics from a fetched <see cref="GenerationStats"/>. The model dimension
    /// prefers the provider-reported model from the stats record, falling back to the model on the
    /// response and finally to <c>unknown</c>, matching the <c>gen_ai.request.model</c> dimension
    /// used elsewhere in the token pipeline.
    /// </summary>
    private static void EmitMetrics(GenerationStats stats, string? fallbackModel, string agentName)
    {
        var model = stats.Model ?? fallbackModel ?? "unknown";

        var tags = new TagList
        {
            { TokenConventions.GenAiRequestModel, model },
            { AgentConventions.Name, agentName }
        };

        if (stats.CacheReadTokens > 0)
            LlmUsageMetrics.CacheReadTokens.Add(stats.CacheReadTokens, tags);

        // cache_discount is reported as a magnitude of the USD saved by the cache hit.
        if (stats.CacheDiscount != 0m)
            LlmUsageMetrics.CacheSavings.Add((double)Math.Abs(stats.CacheDiscount), tags);

        // Hit rate over total native prompt tokens (which include the cached portion). Recorded
        // only when there is a denominator, so a stats record with no prompt tokens is ignored
        // rather than dividing by zero.
        if (stats.PromptTokens > 0)
        {
            var hitRate = (double)stats.CacheReadTokens / stats.PromptTokens;
            LlmUsageMetrics.CacheHitRate.Record(
                hitRate, new TagList { { TokenConventions.GenAiRequestModel, model } });
        }
    }
}
