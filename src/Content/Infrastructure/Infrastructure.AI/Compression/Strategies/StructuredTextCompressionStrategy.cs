using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compression;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;

namespace Infrastructure.AI.Compression.Strategies;

/// <summary>
/// Compresses structured text (file content, search results, tabular data)
/// by deduplicating repeated lines and preserving head/tail with a summary
/// of omitted content.
/// </summary>
/// <remarks>
/// Two-pass compression pipeline:
/// <list type="number">
///   <item><description>Deduplication — consecutive identical lines are collapsed into a count marker.</description></item>
///   <item><description>Head/tail preservation — when still over budget after dedup, keeps first 40
///   and last 10 lines with an omission summary in between.</description></item>
/// </list>
/// Returns <c>WasCompressed = false</c> when the original is already within the token threshold.
/// </remarks>
public sealed class StructuredTextCompressionStrategy : ICompressionStrategy
{
    private const int DefaultHeadLines = 40;
    private const int DefaultTailLines = 10;

    /// <inheritdoc />
    public bool CanHandle(ToolOutputCategory category) =>
        category is ToolOutputCategory.FileContent
            or ToolOutputCategory.SearchResults
            or ToolOutputCategory.Tabular;

    /// <inheritdoc />
    public Task<CompressionResult> CompressAsync(
        string output, int tokenThreshold, CancellationToken cancellationToken = default)
    {
        var originalTokens = TokenEstimationHelper.EstimateTokens(output);

        if (string.IsNullOrEmpty(output) || originalTokens <= tokenThreshold)
            return Task.FromResult(CompressionResult.Passthrough(output, originalTokens));

        var lines = output.Split('\n');

        var deduplicated = DeduplicateLines(lines);
        var deduplicatedText = string.Join('\n', deduplicated);
        var deduplicatedTokens = TokenEstimationHelper.EstimateTokens(deduplicatedText);

        if (deduplicatedTokens <= tokenThreshold)
        {
            return Task.FromResult(new CompressionResult
            {
                Output = deduplicatedText,
                OriginalTokens = originalTokens,
                CompressedTokens = deduplicatedTokens,
                Strategy = "StructuredText",
                WasCompressed = true
            });
        }

        var headTail = HeadTailPreserve(deduplicated, DefaultHeadLines, DefaultTailLines);
        var compressed = string.Join('\n', headTail);
        var compressedTokens = TokenEstimationHelper.EstimateTokens(compressed);

        return Task.FromResult(new CompressionResult
        {
            Output = compressed,
            OriginalTokens = originalTokens,
            CompressedTokens = compressedTokens,
            Strategy = "StructuredText",
            WasCompressed = true
        });
    }

    private static List<string> DeduplicateLines(string[] lines)
    {
        var result = new List<string>();
        var consecutiveCount = 1;
        string? previousLine = null;

        foreach (var line in lines)
        {
            if (line == previousLine)
            {
                consecutiveCount++;
                continue;
            }

            if (consecutiveCount > 1 && previousLine is not null)
                result.Add($"[... {consecutiveCount - 1} similar lines omitted]");

            result.Add(line);
            previousLine = line;
            consecutiveCount = 1;
        }

        if (consecutiveCount > 1)
            result.Add($"[... {consecutiveCount - 1} similar lines omitted]");

        return result;
    }

    private static List<string> HeadTailPreserve(List<string> lines, int headCount, int tailCount)
    {
        if (lines.Count <= headCount + tailCount)
            return lines;

        var result = new List<string>();
        result.AddRange(lines.Take(headCount));

        var omitted = lines.Count - headCount - tailCount;
        result.Add($"[... {omitted} lines omitted]");

        result.AddRange(lines.Skip(lines.Count - tailCount));

        return result;
    }
}
