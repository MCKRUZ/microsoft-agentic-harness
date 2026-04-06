using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.Common.Helpers;

/// <summary>
/// Recursively alphabetizes JSON object properties for deterministic output.
/// </summary>
public static class JsonAlphabetizerHelper
{
    private static readonly JsonSerializerOptions s_indentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Parses a JSON string, alphabetizes all object properties recursively
    /// (case-insensitive), and returns the formatted result.
    /// </summary>
    public static string AlphabetizeProperties(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var node = JsonNode.Parse(json)
            ?? throw new JsonException("Failed to parse JSON input.");

        var sorted = SortNode(node);
        return sorted.ToJsonString(s_indentedOptions);
    }

    private static JsonNode SortNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var property in obj.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                sorted.Add(property.Key, SortNode(property.Value?.DeepClone()));
            }
            return sorted;
        }

        if (node is JsonArray array)
        {
            var sorted = new JsonArray();
            foreach (var item in array)
            {
                sorted.Add(SortNode(item?.DeepClone()));
            }
            return sorted;
        }

        return node?.DeepClone()!;
    }
}
