using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Span processor that reads <c>gen_ai.usage.*</c> token attributes from
/// completed LLM call spans and records cost/usage metrics. Inspired by
/// Nexus token analytics which tracks per-model spend, cache savings,
/// and efficiency ratios.
/// </summary>
/// <remarks>
/// <para>
/// Reads these span attributes (set by AI framework instrumentation):
/// <list type="bullet">
///   <item><c>gen_ai.usage.input_tokens</c></item>
///   <item><c>gen_ai.usage.output_tokens</c></item>
///   <item><c>gen_ai.usage.cache_read_input_tokens</c></item>
///   <item><c>gen_ai.usage.cache_creation_input_tokens</c></item>
///   <item><c>gen_ai.request.model</c></item>
/// </list>
/// </para>
/// <para>
/// Computes estimated cost from <see cref="LlmPricingConfig"/> and records
/// to <see cref="LlmUsageMetrics"/> counters and histograms.
/// </para>
/// </remarks>
public sealed class LlmTokenTrackingProcessor : BaseProcessor<Activity>
{
    private readonly ILogger<LlmTokenTrackingProcessor> _logger;
    private readonly IBudgetTrackingService _budgetTracker;
    private readonly Dictionary<string, ModelPricingEntry> _pricingLookup;
    private readonly string _defaultModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmTokenTrackingProcessor"/> class.
    /// </summary>
    public LlmTokenTrackingProcessor(
        ILogger<LlmTokenTrackingProcessor> logger,
        IOptions<Domain.Common.Config.AppConfig> appConfig,
        IBudgetTrackingService budgetTracker)
    {
        _logger = logger;
        _budgetTracker = budgetTracker;
        var config = appConfig.Value.Observability.LlmPricing;
        _defaultModel = config.DefaultModel;

        _pricingLookup = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in config.Models)
        {
            _pricingLookup[model.Name] = model;
        }

        _logger.LogInformation(
            "LLM token tracking initialized with {ModelCount} pricing entries, default={DefaultModel}",
            _pricingLookup.Count, _defaultModel);
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        // Only process spans that have token usage attributes
        if (data.GetTagItem(TokenConventions.GenAiInputTokens) is not (int or long))
            return;

        var inputTokens = GetLongTag(data, TokenConventions.GenAiInputTokens);
        var outputTokens = GetLongTag(data, TokenConventions.GenAiOutputTokens);
        var cacheReadTokens = GetLongTag(data, TokenConventions.GenAiCacheReadTokens);
        var cacheWriteTokens = GetLongTag(data, TokenConventions.GenAiCacheWriteTokens);
        var model = data.GetTagItem(TokenConventions.GenAiRequestModel) as string ?? _defaultModel;
        var agentName = data.GetTagItem(AgentConventions.Name) as string ?? "unknown";

        var tags = new TagList
        {
            { TokenConventions.GenAiRequestModel, model },
            { AgentConventions.Name, agentName }
        };

        // Record cache tokens
        if (cacheReadTokens > 0)
            LlmUsageMetrics.CacheReadTokens.Add(cacheReadTokens, tags);
        if (cacheWriteTokens > 0)
            LlmUsageMetrics.CacheWriteTokens.Add(cacheWriteTokens, tags);

        // Compute and record cost
        // Note: gen_ai.usage.input_tokens is the non-cached input portion.
        // Cache-read tokens are reported separately and priced at the lower cache rate.
        if (_pricingLookup.TryGetValue(model, out var pricing))
        {
            var cost = ComputeCost(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, pricing);
            if (cost > 0)
            {
                LlmUsageMetrics.EstimatedCost.Add(cost, tags);
                LlmUsageMetrics.CostPerTurn.Record(cost, new TagList { { AgentConventions.Name, agentName } });
                _budgetTracker.RecordSpend(cost, agentName);
            }

            // Cache savings = what cache-read tokens would have cost at full input price
            if (cacheReadTokens > 0)
            {
                var savings = (double)(cacheReadTokens * (pricing.InputPerMillion - pricing.CacheReadPerMillion) / 1_000_000m);
                if (savings > 0)
                    LlmUsageMetrics.CacheSavings.Add(savings, tags);
            }
        }

        // Cache hit rate
        var totalInput = inputTokens + cacheReadTokens;
        if (totalInput > 0)
        {
            var hitRate = (double)cacheReadTokens / totalInput;
            LlmUsageMetrics.CacheHitRate.Record(hitRate, new TagList { { TokenConventions.GenAiRequestModel, model } });
        }

        // Per-turn metrics
        var totalTokens = inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens;
        LlmUsageMetrics.TokensPerTurn.Record(totalTokens, new TagList { { AgentConventions.Name, agentName } });

        // Agent-level token breakdowns (supplements gen_ai.client.token.usage with agent context)
        TokenUsageMetrics.InputTokens.Record(inputTokens, tags);
        TokenUsageMetrics.OutputTokens.Record(outputTokens, tags);
        TokenUsageMetrics.TotalTokens.Record(totalTokens, tags);

        // Per-user token and cost tracking (user_id propagated via baggage from the hub)
        var userId = data.GetBaggageItem(UserConventions.UserId);
        if (!string.IsNullOrEmpty(userId))
        {
            var userTag = new KeyValuePair<string, object?>(UserConventions.UserId, userId);
            var userAgentTag = new KeyValuePair<string, object?>(AgentConventions.Name, agentName);
            UserActivityMetrics.TokensConsumed.Add(totalTokens, userTag, userAgentTag);

            if (_pricingLookup.TryGetValue(model, out var userPricing))
            {
                var userCost = ComputeCost(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, userPricing);
                if (userCost > 0)
                    UserActivityMetrics.CostAccrued.Add(userCost, userTag, userAgentTag);
            }
        }
    }

    private static double ComputeCost(
        long input, long output, long cacheRead, long cacheWrite,
        ModelPricingEntry pricing)
    {
        return (double)(
            (input * pricing.InputPerMillion / 1_000_000m) +
            (output * pricing.OutputPerMillion / 1_000_000m) +
            (cacheRead * pricing.CacheReadPerMillion / 1_000_000m) +
            (cacheWrite * pricing.CacheWritePerMillion / 1_000_000m));
    }

    private static long GetLongTag(Activity data, string key)
    {
        return data.GetTagItem(key) switch
        {
            long l => l,
            int i => i,
            _ => 0
        };
    }
}
