using Domain.Common.Config.Observability;

namespace Application.AI.Common.Pricing;

/// <summary>
/// Pure USD cost arithmetic for LLM token usage. Centralizes the per-million pricing formula so the
/// usage-capture accumulator, the token-tracking span processor, and the cache-stats enrichment
/// middleware all price a call identically instead of each carrying its own copy.
/// </summary>
public static class LlmCostCalculator
{
    /// <summary>
    /// Computes the USD cost of one call's token usage at the given model's per-million rates.
    /// </summary>
    /// <param name="inputTokens">Non-cached input (prompt) tokens.</param>
    /// <param name="outputTokens">Output (completion) tokens.</param>
    /// <param name="cacheReadTokens">Tokens served from the prompt cache (billed at the cache-read rate).</param>
    /// <param name="cacheWriteTokens">Tokens written to the prompt cache (billed at the cache-write rate).</param>
    /// <param name="pricing">The model's per-million pricing entry.</param>
    /// <returns>The total cost in USD.</returns>
    public static decimal Compute(
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        ModelPricingEntry pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        return
            (inputTokens * pricing.InputPerMillion / 1_000_000m) +
            (outputTokens * pricing.OutputPerMillion / 1_000_000m) +
            (cacheReadTokens * pricing.CacheReadPerMillion / 1_000_000m) +
            (cacheWriteTokens * pricing.CacheWritePerMillion / 1_000_000m);
    }

    /// <summary>
    /// Resolves the pricing entry for a model, falling back to the configured default model when the
    /// requested model is null or absent from the table (e.g. a deployment alias or an unpriced
    /// model). Returns <see langword="null"/> only when neither the model nor the default is present.
    /// </summary>
    /// <param name="pricingByModel">Lookup of model name to pricing entry.</param>
    /// <param name="model">The requested model, or null.</param>
    /// <param name="defaultModel">The configured default model to fall back to.</param>
    /// <returns>The resolved pricing entry, or null when none can be resolved.</returns>
    public static ModelPricingEntry? ResolvePricing(
        IReadOnlyDictionary<string, ModelPricingEntry> pricingByModel,
        string? model,
        string defaultModel)
    {
        ArgumentNullException.ThrowIfNull(pricingByModel);
        if (model is not null && pricingByModel.TryGetValue(model, out var entry))
            return entry;
        return pricingByModel.TryGetValue(defaultModel, out var fallback) ? fallback : null;
    }
}
