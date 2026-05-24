using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compression;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Compression.Strategies;

/// <summary>
/// Compresses unstructured text using sentence-boundary truncation.
/// When heuristic truncation still exceeds the threshold and LLM fallback
/// is enabled, calls an economy-tier model for intelligent summarization.
/// Falls back to hard truncation if the LLM call fails or is disabled.
/// </summary>
/// <remarks>
/// Compression pipeline:
/// <list type="number">
///   <item><description>Sentence-boundary truncation — clips at the last complete sentence before the target char limit.</description></item>
///   <item><description>LLM summarization (optional) — economy-tier model call when sentence truncation still exceeds the threshold.</description></item>
///   <item><description>Hard truncation — character-level cut with <c>...[truncated]</c> suffix as final fallback.</description></item>
/// </list>
/// </remarks>
public sealed class FreeTextCompressionStrategy : ICompressionStrategy
{
    private readonly IModelRouter _modelRouter;
    private readonly ToolOutputCompressionConfig _config;
    private readonly ILogger<FreeTextCompressionStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FreeTextCompressionStrategy"/>.
    /// </summary>
    /// <param name="modelRouter">Router for resolving the economy-tier LLM used during fallback summarization.</param>
    /// <param name="config">Compression configuration (LLM fallback toggle, timeout, routing operation name).</param>
    /// <param name="logger">Logger for recording fallback events and warnings.</param>
    public FreeTextCompressionStrategy(
        IModelRouter modelRouter,
        IOptions<ToolOutputCompressionConfig> config,
        ILogger<FreeTextCompressionStrategy> logger)
    {
        _modelRouter = modelRouter;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(ToolOutputCategory category) => category == ToolOutputCategory.FreeText;

    /// <inheritdoc />
    public async Task<CompressionResult> CompressAsync(
        string output, int tokenThreshold, CancellationToken cancellationToken = default)
    {
        var originalTokens = TokenEstimationHelper.EstimateTokens(output);

        if (string.IsNullOrEmpty(output) || originalTokens <= tokenThreshold)
            return CompressionResult.Passthrough(output, originalTokens);

        var truncated = TruncateAtSentenceBoundary(output, tokenThreshold);
        var truncatedTokens = TokenEstimationHelper.EstimateTokens(truncated);

        if (truncatedTokens <= tokenThreshold)
        {
            return new CompressionResult
            {
                Output = truncated,
                OriginalTokens = originalTokens,
                CompressedTokens = truncatedTokens,
                Strategy = "FreeText",
                WasCompressed = true
            };
        }

        if (_config.LlmFallbackEnabled)
        {
            try
            {
                var llmResult = await SummarizeWithLlmAsync(output, tokenThreshold, cancellationToken);
                if (llmResult is not null)
                {
                    return new CompressionResult
                    {
                        Output = llmResult,
                        OriginalTokens = originalTokens,
                        CompressedTokens = TokenEstimationHelper.EstimateTokens(llmResult),
                        Strategy = "LlmFallback",
                        WasCompressed = true
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "LLM compression fallback failed, using hard truncation");
            }
        }

        var hardTruncated = TokenEstimationHelper.TruncateToTokenBudget(output, tokenThreshold);
        return new CompressionResult
        {
            Output = hardTruncated,
            OriginalTokens = originalTokens,
            CompressedTokens = TokenEstimationHelper.EstimateTokens(hardTruncated),
            Strategy = "HardTruncate",
            WasCompressed = true
        };
    }

    /// <summary>
    /// Clips the text at the last sentence-ending punctuation before the target
    /// character limit, then appends an omission notice. The target is set at
    /// 60% of the full token budget (~4 chars/token) to leave room for the omission
    /// marker and to avoid re-triggering LLM fallback in common cases.
    /// </summary>
    private static string TruncateAtSentenceBoundary(string text, int tokenThreshold)
    {
        const double targetFactor = 0.6;
        const int charsPerToken = 4;
        var targetChars = (int)(tokenThreshold * charsPerToken * targetFactor);

        if (text.Length <= targetChars)
            return text;

        var lastEnd = 0;
        for (var i = 0; i < text.Length && i < targetChars; i++)
        {
            if (i + 2 <= text.Length)
            {
                var twoChar = text.Substring(i, 2);
                if (twoChar is ". " or "! " or "? " or ".\n" or "!\n" or "?\n")
                    lastEnd = i + 2;
            }
        }

        if (lastEnd == 0)
            lastEnd = Math.Min(targetChars, text.Length);

        return text[..lastEnd] + "[... remainder omitted]";
    }

    private async Task<string?> SummarizeWithLlmAsync(
        string input, int tokenThreshold, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.LlmFallbackTimeoutSeconds));

        var decision = await _modelRouter.RouteOperationAsync(
            _config.LlmRoutingOperation, cts.Token);

        var prompt = $"Summarize this tool output in under {tokenThreshold} tokens. " +
                     "Preserve actionable information, specific values, and error details. " +
                     "Omit boilerplate.\n\n" + input;

        var response = await decision.Client.GetResponseAsync(prompt, cancellationToken: cts.Token);
        return response.Text;
    }
}
