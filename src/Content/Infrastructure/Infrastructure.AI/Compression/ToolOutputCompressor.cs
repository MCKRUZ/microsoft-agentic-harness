using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compression;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Compression;

/// <summary>
/// Orchestrates tool output compression by dispatching to category-matched
/// strategies with fallback to FreeText and hard truncation as last resort.
/// </summary>
/// <remarks>
/// When the <paramref name="category"/> passed to <see cref="CompressAsync"/> is null,
/// the compressor calls <see cref="ContentTypeDetector.Detect"/> to infer the category
/// from the output content. This keeps the Application layer free of Infrastructure dependencies.
/// </remarks>
public sealed class ToolOutputCompressor : IToolOutputCompressor
{
    private readonly IReadOnlyList<ICompressionStrategy> _strategies;
    private readonly ILogger<ToolOutputCompressor> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="ToolOutputCompressor"/>.
    /// </summary>
    /// <param name="strategies">All registered compression strategies, resolved in priority order.</param>
    /// <param name="logger">Logger for strategy failure diagnostics.</param>
    public ToolOutputCompressor(
        IEnumerable<ICompressionStrategy> strategies,
        ILogger<ToolOutputCompressor> logger)
    {
        _strategies = strategies.ToList();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CompressionResult> CompressAsync(
        string output,
        ToolOutputCategory? category,
        int tokenThreshold,
        CancellationToken cancellationToken = default)
    {
        var originalTokens = TokenEstimationHelper.EstimateTokens(output);

        if (string.IsNullOrEmpty(output) || originalTokens <= tokenThreshold)
            return CompressionResult.Passthrough(output, originalTokens);

        var resolvedCategory = category ?? ContentTypeDetector.Detect(output);

        var matched = _strategies.FirstOrDefault(s => s.CanHandle(resolvedCategory));

        if (matched is not null)
        {
            try
            {
                var result = await matched.CompressAsync(output, tokenThreshold, cancellationToken);
                if (result.WasCompressed)
                    return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Strategy {Strategy} failed for category {Category}, falling back",
                    matched.GetType().Name, resolvedCategory);
            }
        }

        if (resolvedCategory != ToolOutputCategory.FreeText)
        {
            var freeTextStrategy = _strategies.FirstOrDefault(s => s.CanHandle(ToolOutputCategory.FreeText));
            if (freeTextStrategy is not null)
            {
                try
                {
                    var fallbackResult = await freeTextStrategy.CompressAsync(
                        output, tokenThreshold, cancellationToken);
                    if (fallbackResult.WasCompressed)
                        return fallbackResult;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "FreeText fallback strategy also failed, hard truncating");
                }
            }
        }

        var truncated = TokenEstimationHelper.TruncateToTokenBudget(output, tokenThreshold);
        return new CompressionResult
        {
            Output = truncated,
            OriginalTokens = originalTokens,
            CompressedTokens = TokenEstimationHelper.EstimateTokens(truncated),
            Strategy = "HardTruncate",
            WasCompressed = true
        };
    }
}
