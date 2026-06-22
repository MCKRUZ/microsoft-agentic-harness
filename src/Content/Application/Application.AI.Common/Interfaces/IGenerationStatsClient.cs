namespace Application.AI.Common.Interfaces;

/// <summary>
/// Fetches post-call usage and cost metadata for a single LLM generation from a provider's
/// out-of-band statistics endpoint (e.g. OpenRouter's <c>GET /generation?id=</c>).
/// </summary>
/// <remarks>
/// <para>
/// Some providers do not surface cache token counts on the inline chat-completions response —
/// in particular, OpenAI-compatible gateways report <c>cached_tokens</c> only in a separate
/// generation-stats record that becomes available a few seconds after the call completes, and
/// never in the streamed SSE body. The harness's typed pipeline (Microsoft.Extensions.AI's
/// <c>UsageDetails.AdditionalCounts</c>) does not expose those fields for this shape, so cache
/// telemetry has to be retrieved here instead.
/// </para>
/// <para>
/// Implementations are expected to poll the endpoint (the record may 404 immediately after the
/// call and populate shortly after) and to fail soft: a null return means "stats unavailable",
/// never an exception that could disrupt the agent turn. Callers invoke this off the response
/// path, so latency is tolerable but bounded.
/// </para>
/// </remarks>
public interface IGenerationStatsClient
{
    /// <summary>
    /// Retrieves usage and cost statistics for the generation identified by
    /// <paramref name="generationId"/> (the provider's response identifier).
    /// </summary>
    /// <param name="generationId">
    /// The provider-assigned generation id, taken from <c>ChatResponse.ResponseId</c>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the (polling) lookup.</param>
    /// <returns>
    /// The parsed <see cref="GenerationStats"/>, or <see langword="null"/> when the record could
    /// not be retrieved (still unavailable after retries, an empty id, or a transport/parse error).
    /// </returns>
    Task<GenerationStats?> GetGenerationStatsAsync(string generationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable usage and cost statistics for a single LLM generation, normalized from a provider's
/// generation-stats endpoint.
/// </summary>
/// <param name="Model">
/// The model the provider reported serving the generation (e.g. <c>anthropic/claude-sonnet-4.6</c>),
/// or <see langword="null"/> when absent. Used as the <c>gen_ai.request.model</c> metric dimension.
/// </param>
/// <param name="CacheReadTokens">
/// Prompt tokens served from the provider's prompt cache (<c>native_tokens_cached</c>). Zero when
/// the prefix was not cached or caching is disabled.
/// </param>
/// <param name="PromptTokens">
/// Total native prompt tokens for the call (<c>native_tokens_prompt</c>), inclusive of
/// <see cref="CacheReadTokens"/>. The denominator for the cache hit rate.
/// </param>
/// <param name="TotalCost">
/// The provider's authoritative charge for the generation in USD (<c>total_cost</c>), already net
/// of any cache discount. Surfaced as the <c>agent.tokens.cost_actual</c> metric, which the cost
/// tiles prefer over the estimate on this path.
/// </param>
/// <param name="CacheDiscount">
/// The USD amount the prompt cache saved on this generation (<c>cache_discount</c>). Reported as a
/// magnitude; zero when nothing was served from cache.
/// </param>
public record GenerationStats(
    string? Model,
    long CacheReadTokens,
    long PromptTokens,
    decimal TotalCost,
    decimal CacheDiscount);
