using System.Diagnostics;
using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Pricing;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Middleware;

/// <summary>
/// Chat client middleware that records prompt-cache telemetry and the authoritative per-call cost
/// for providers whose cache token counts are only available out-of-band (notably the
/// OpenAI-compatible OpenRouter path).
/// </summary>
/// <remarks>
/// <para>
/// After each provider call it captures the response identifier and, on a background task, fetches
/// the generation's usage record via <see cref="IGenerationStatsClient"/> and emits
/// <see cref="LlmUsageMetrics.CacheReadTokens"/>, <see cref="LlmUsageMetrics.CacheHitRate"/>,
/// <see cref="LlmUsageMetrics.CacheSavings"/>, and <see cref="LlmUsageMetrics.ActualCost"/>. These
/// are the inputs the dashboard Cache Read / Cache Efficiency / Cache Savings / Cost tiles were
/// built for but never received on this path, because the inline usage object does not carry the
/// cache fields and the streamed body cannot be read.
/// </para>
/// <para>
/// <b>Cost is emitted for every call</b>, not only when the stats fetch succeeds: when the
/// generation record is available the provider's real, cache-discounted <c>total_cost</c> is used;
/// when it is not (the fetch is fail-soft and the record may never land), the per-call cost is
/// estimated from the response token counts and the pricing table. This keeps
/// <c>agent.tokens.cost_actual</c> complete, so the cost tiles — which prefer it over the estimate —
/// can never silently under-report spend for a call whose record failed to arrive.
/// </para>
/// <para>
/// The fetch runs off the response path (the stats record lags the call by a few seconds and the
/// client polls for it) so it never adds latency to the agent turn, and it fails soft — any error
/// is logged at debug and swallowed. The cache instruments emitted here are intentionally the
/// <em>only</em> source of cache telemetry on this path; <c>LlmTokenTrackingProcessor</c> stays
/// silent on the cache instruments when the span carries no cache attributes, so there is no double
/// counting.
/// </para>
/// </remarks>
public sealed class CacheStatsEnrichingChatClient : DelegatingChatClient
{
    private readonly IGenerationStatsClient _statsClient;
    private readonly string _agentName;
    private readonly IReadOnlyDictionary<string, ModelPricingEntry> _pricingByModel;
    private readonly string _defaultModel;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheStatsEnrichingChatClient"/> class.
    /// </summary>
    /// <param name="innerClient">The inner chat client to wrap.</param>
    /// <param name="statsClient">Client used to fetch the out-of-band generation usage record.</param>
    /// <param name="agentName">Agent name used as the <c>agent.name</c> metric dimension.</param>
    /// <param name="pricingByModel">Per-model pricing used to estimate cost when the real record is unavailable.</param>
    /// <param name="defaultModel">Model whose pricing is used when the call's model is null or unpriced.</param>
    /// <param name="logger">Logger for diagnostic (debug-level) failures.</param>
    public CacheStatsEnrichingChatClient(
        IChatClient innerClient,
        IGenerationStatsClient statsClient,
        string agentName,
        IReadOnlyDictionary<string, ModelPricingEntry> pricingByModel,
        string defaultModel,
        ILogger<CacheStatsEnrichingChatClient> logger)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(statsClient);
        ArgumentNullException.ThrowIfNull(pricingByModel);
        ArgumentNullException.ThrowIfNull(logger);
        _statsClient = statsClient;
        _agentName = string.IsNullOrWhiteSpace(agentName) ? "unknown" : agentName;
        _pricingByModel = pricingByModel;
        _defaultModel = defaultModel;
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
        EnrichInBackground(response.ResponseId, response.ModelId, ExtractTokens(response.Usage));
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Accumulate updates so the coalesced response surfaces the id, model, and usage exactly as
        // the non-streaming path does — the streamed body itself carries no cache fields.
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        var response = updates.ToChatResponse();
        EnrichInBackground(response.ResponseId, response.ModelId, ExtractTokens(response.Usage));
    }

    /// <summary>
    /// Starts a background fetch of the generation stats and emits the cache + cost metrics. Returns
    /// immediately; failures are logged at debug and never propagate to the caller. Runs even when no
    /// response id is available so the estimated cost is still emitted.
    /// </summary>
    private void EnrichInBackground(string? responseId, string? modelId, TokenCounts tokens)
    {
        _enrichmentCompletion = Task.Run(async () =>
        {
            try
            {
                var stats = string.IsNullOrEmpty(responseId)
                    ? null
                    : await _statsClient
                        .GetGenerationStatsAsync(responseId, CancellationToken.None)
                        .ConfigureAwait(false);

                EmitMetrics(stats, modelId, tokens);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Failed to enrich cache telemetry for generation {ResponseId}.", responseId);
            }
        });
    }

    /// <summary>
    /// Emits the cache and cost metrics. Cache instruments are recorded only from a fetched
    /// <see cref="GenerationStats"/>; cost is always emitted — the provider's real cost when the
    /// record is available, otherwise the estimate computed from the call's token counts.
    /// </summary>
    private void EmitMetrics(GenerationStats? stats, string? fallbackModel, TokenCounts tokens)
    {
        var model = stats?.Model ?? fallbackModel;
        var tags = new TagList
        {
            { TokenConventions.GenAiRequestModel, model ?? "unknown" },
            { AgentConventions.Name, _agentName }
        };

        if (stats is not null)
        {
            if (stats.CacheReadTokens > 0)
                LlmUsageMetrics.CacheReadTokens.Add(stats.CacheReadTokens, tags);

            // cache_discount is reported as a magnitude of the USD saved by the cache hit.
            if (stats.CacheDiscount != 0m)
                LlmUsageMetrics.CacheSavings.Add((double)Math.Abs(stats.CacheDiscount), tags);

            // Hit rate over total native prompt tokens (which include the cached portion). Recorded
            // only when there is a denominator, so a record with no prompt tokens divides nothing.
            if (stats.PromptTokens > 0)
            {
                var hitRate = (double)stats.CacheReadTokens / stats.PromptTokens;
                LlmUsageMetrics.CacheHitRate.Record(
                    hitRate, new TagList { { TokenConventions.GenAiRequestModel, model ?? "unknown" } });
            }
        }

        // Prefer the provider's authoritative, already-discounted cost; fall back to the estimate so
        // the metric covers every call (the estimate over-prices cached tokens — the conservative
        // direction — but it is never silently absent).
        var cost = stats is { TotalCost: > 0m } ? stats.TotalCost : EstimateCost(tokens, model);
        if (cost > 0m)
            LlmUsageMetrics.ActualCost.Add((double)cost, tags);
    }

    /// <summary>Estimates a call's USD cost from its token counts and the pricing table.</summary>
    private decimal EstimateCost(TokenCounts tokens, string? model)
    {
        var pricing = LlmCostCalculator.ResolvePricing(_pricingByModel, model, _defaultModel);
        return pricing is null
            ? 0m
            : LlmCostCalculator.Compute(tokens.Input, tokens.Output, tokens.CacheRead, tokens.CacheWrite, pricing);
    }

    private static TokenCounts ExtractTokens(UsageDetails? usage)
    {
        if (usage is null)
            return default;

        return new TokenCounts(
            usage.InputTokenCount ?? 0,
            usage.OutputTokenCount ?? 0,
            GetAdditionalCount(usage, "cache_read_input_tokens"),
            GetAdditionalCount(usage, "cache_creation_input_tokens"));
    }

    private static long GetAdditionalCount(UsageDetails usage, string key)
        => usage.AdditionalCounts?.TryGetValue(key, out var value) == true ? value : 0;

    /// <summary>Per-call token counts captured from the response usage.</summary>
    private readonly record struct TokenCounts(long Input, long Output, long CacheRead, long CacheWrite);
}
