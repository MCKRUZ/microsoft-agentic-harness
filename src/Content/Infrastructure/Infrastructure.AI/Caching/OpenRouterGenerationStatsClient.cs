using System.Net;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Caching;

/// <summary>
/// <see cref="IGenerationStatsClient"/> backed by OpenRouter's
/// <c>GET {endpoint}/generation?id={id}</c> endpoint, which returns per-generation native token
/// counts (including <c>native_tokens_cached</c>) and the real, cache-discounted cost.
/// </summary>
/// <remarks>
/// <para>
/// This is the only reliable source of cache telemetry on the OpenAI-compatible OpenRouter path:
/// the inline chat-completions usage object surfaced by Microsoft.Extensions.AI does not carry the
/// cache fields for this shape, and the streamed SSE body cannot be read without breaking the
/// stream. The generation record is therefore fetched out-of-band after the call.
/// </para>
/// <para>
/// The record is not written synchronously with the response — a lookup immediately after the call
/// typically returns <c>404</c> and the data appears a few seconds later. This client polls past
/// the 404 with a bounded number of attempts, and returns <see langword="null"/> rather than
/// throwing on any failure so that a missing stats record never disrupts the agent turn.
/// </para>
/// <para>
/// Registered as a typed <see cref="HttpClient"/> whose <c>BaseAddress</c> is the configured
/// OpenRouter endpoint (with a trailing slash) and whose <c>Authorization</c> header carries the
/// API key. See <c>Infrastructure.AI.DependencyInjection.RegisterGenerationStatsClient</c>.
/// </para>
/// </remarks>
public sealed class OpenRouterGenerationStatsClient : IGenerationStatsClient
{
    private const int DefaultMaxAttempts = 6;
    private static readonly TimeSpan s_defaultRetryDelay = TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenRouterGenerationStatsClient> _logger;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenRouterGenerationStatsClient"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// Typed client preconfigured with the OpenRouter base address and bearer authorization.
    /// </param>
    /// <param name="logger">Logger for diagnostic (debug-level) failures.</param>
    public OpenRouterGenerationStatsClient(
        HttpClient httpClient,
        ILogger<OpenRouterGenerationStatsClient> logger)
        : this(httpClient, logger, DefaultMaxAttempts, s_defaultRetryDelay)
    {
    }

    /// <summary>
    /// Test/advanced constructor allowing the polling cadence to be tuned.
    /// </summary>
    internal OpenRouterGenerationStatsClient(
        HttpClient httpClient,
        ILogger<OpenRouterGenerationStatsClient> logger,
        int maxAttempts,
        TimeSpan retryDelay)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryDelay = retryDelay;
    }

    /// <inheritdoc />
    public async Task<GenerationStats?> GetGenerationStatsAsync(
        string generationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(generationId))
            return null;

        var requestUri = $"generation?id={Uri.EscapeDataString(generationId)}";

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                using var response = await _httpClient
                    .GetAsync(requestUri, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                // The record is written asynchronously after the call — a 404 means "not ready yet",
                // so back off and retry rather than giving up.
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (attempt < _maxAttempts)
                        await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "Generation stats lookup for {GenerationId} returned {StatusCode}; skipping.",
                        generationId, (int)response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Parse(json, generationId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Generation stats lookup for {GenerationId} failed on attempt {Attempt}.",
                    generationId, attempt);
                return null;
            }
        }

        _logger.LogDebug(
            "Generation stats for {GenerationId} were not available after {Attempts} attempts.",
            generationId, _maxAttempts);
        return null;
    }

    /// <summary>
    /// Parses the OpenRouter generation record (payload nested under a top-level <c>data</c> object)
    /// into a <see cref="GenerationStats"/>. Returns <see langword="null"/> when the envelope is
    /// missing or malformed.
    /// </summary>
    private GenerationStats? Parse(string json, string generationId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                _logger.LogDebug(
                    "Generation stats for {GenerationId} had no 'data' object; skipping.", generationId);
                return null;
            }

            var model = data.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            return new GenerationStats(
                Model: model,
                CacheReadTokens: GetLong(data, "native_tokens_cached"),
                PromptTokens: GetLong(data, "native_tokens_prompt"),
                TotalCost: GetDecimal(data, "total_cost"),
                CacheDiscount: GetDecimal(data, "cache_discount"));
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex,
                "Generation stats for {GenerationId} could not be parsed; skipping.", generationId);
            return null;
        }
    }

    private static long GetLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)
            ? v
            : 0L;

    private static decimal GetDecimal(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var v)
            ? v
            : 0m;
}
