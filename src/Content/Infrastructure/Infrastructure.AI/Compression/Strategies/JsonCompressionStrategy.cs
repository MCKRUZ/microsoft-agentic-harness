using System.Text.Json;
using System.Text.Json.Nodes;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compression;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;

namespace Infrastructure.AI.Compression.Strategies;

/// <summary>
/// Compresses JSON output by truncating large arrays, pruning deep nesting,
/// and removing low-signal keys. Returns <c>WasCompressed = false</c> on
/// parse failure to signal fallthrough to the next strategy.
/// </summary>
/// <remarks>
/// Three-pass compression pipeline:
/// <list type="number">
///   <item><description>Array truncation — arrays &gt;10 elements become first 3 + last 2 with omission count.</description></item>
///   <item><description>Depth pruning — objects nested beyond depth 4 are replaced with a summary placeholder.</description></item>
///   <item><description>Low-signal key removal — metadata, pagination, and link fields are stripped when still over budget.</description></item>
/// </list>
/// On JSON parse failure the strategy returns <c>WasCompressed = false</c> so the
/// compressor falls through to the next registered strategy.
/// </remarks>
public sealed class JsonCompressionStrategy : ICompressionStrategy
{
    private const int MaxArrayElements = 10;
    private const int KeepFirst = 3;
    private const int KeepLast = 2;
    private const int MaxDepth = 4;

    private static readonly HashSet<string> LowSignalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata", "_links", "pagination", "headers", "timestamps",
        "_metadata", "links", "paging", "cursor"
    };

    /// <inheritdoc />
    public bool CanHandle(ToolOutputCategory category) => category == ToolOutputCategory.Json;

    /// <inheritdoc />
    public Task<CompressionResult> CompressAsync(
        string output, int tokenThreshold, CancellationToken cancellationToken = default)
    {
        var originalTokens = TokenEstimationHelper.EstimateTokens(output);

        if (string.IsNullOrEmpty(output))
            return Task.FromResult(CompressionResult.Passthrough(output, originalTokens));

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(output);
        }
        catch (JsonException)
        {
            return Task.FromResult(new CompressionResult
            {
                Output = output,
                OriginalTokens = originalTokens,
                CompressedTokens = originalTokens,
                Strategy = "Json",
                WasCompressed = false
            });
        }

        if (node is null)
            return Task.FromResult(CompressionResult.Passthrough(output, originalTokens));

        // Structural passes always run — they fix depth and array bloat regardless of token budget.
        // This ensures deeply nested or oversized arrays are always normalized.
        TruncateArrays(node);
        PruneDepth(node, 0);

        var afterStructural = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var structurallyChanged = afterStructural != output;

        // If still over budget after structural passes, strip low-signal keys.
        var afterStructuralTokens = TokenEstimationHelper.EstimateTokens(afterStructural);
        if (afterStructuralTokens > tokenThreshold)
        {
            RemoveLowSignalKeys(node);
            afterStructural = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            afterStructuralTokens = TokenEstimationHelper.EstimateTokens(afterStructural);
            structurallyChanged = true;
        }

        // Passthrough only when the content was never over budget AND no structural changes occurred.
        if (!structurallyChanged && originalTokens <= tokenThreshold)
            return Task.FromResult(CompressionResult.Passthrough(output, originalTokens));

        return Task.FromResult(new CompressionResult
        {
            Output = afterStructural,
            OriginalTokens = originalTokens,
            CompressedTokens = afterStructuralTokens,
            Strategy = "Json",
            WasCompressed = true
        });
    }

    private static void TruncateArrays(JsonNode node)
    {
        switch (node)
        {
            case JsonArray array when array.Count > MaxArrayElements:
            {
                var omitted = array.Count - KeepFirst - KeepLast;
                var kept = new List<JsonNode?>();
                for (var i = 0; i < KeepFirst && i < array.Count; i++)
                    kept.Add(array[i]?.DeepClone());
                kept.Add(JsonValue.Create($"... ({omitted} items omitted)"));
                for (var i = array.Count - KeepLast; i < array.Count; i++)
                    kept.Add(array[i]?.DeepClone());

                array.Clear();
                foreach (var item in kept)
                    array.Add(item);
                break;
            }
            case JsonArray array:
            {
                foreach (var item in array)
                    if (item is not null) TruncateArrays(item);
                break;
            }
            case JsonObject obj:
            {
                foreach (var (_, value) in obj)
                    if (value is not null) TruncateArrays(value);
                break;
            }
        }
    }

    private static void PruneDepth(JsonNode node, int depth)
    {
        if (node is not JsonObject obj) return;

        var toPrune = new List<string>();
        foreach (var (key, value) in obj)
        {
            if (value is JsonObject child)
            {
                if (depth >= MaxDepth)
                    toPrune.Add(key);
                else
                    PruneDepth(child, depth + 1);
            }
            else if (value is JsonArray arr)
            {
                foreach (var item in arr)
                    if (item is not null) PruneDepth(item, depth + 1);
            }
        }

        foreach (var key in toPrune)
        {
            var child = obj[key] as JsonObject;
            var keyCount = child?.Count ?? 0;
            obj[key] = JsonValue.Create($"[nested object with {keyCount} keys]");
        }
    }

    private static void RemoveLowSignalKeys(JsonNode node)
    {
        if (node is not JsonObject obj) return;

        var toRemove = obj
            .Where(kv => LowSignalKeys.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            obj.Remove(key);

        foreach (var (_, value) in obj)
            if (value is not null) RemoveLowSignalKeys(value);
    }
}
