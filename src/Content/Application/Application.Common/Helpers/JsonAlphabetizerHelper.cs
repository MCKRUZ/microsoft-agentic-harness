using System.Text.Json;
using System.Text.Json.Nodes;

namespace Application.Common.Helpers;

/// <summary>
/// Recursively alphabetizes JSON object properties for deterministic output.
/// Useful for config normalization, manifest comparison, and test consistency.
/// </summary>
public static class JsonAlphabetizerHelper
{
    /// <summary>
    /// Parses a JSON string, alphabetizes all object properties recursively
    /// (case-insensitive), and returns the formatted result.
    /// </summary>
    /// <param name="json">The JSON string to alphabetize.</param>
    /// <returns>A formatted JSON string with properties sorted alphabetically.</returns>
    /// <exception cref="JsonException">Thrown when the input is not valid JSON.</exception>
    /// <example>
    /// <code>
    /// var input = """{"z": 1, "a": {"c": 3, "b": 2}}""";
    /// var sorted = JsonAlphabetizerHelper.AlphabetizeProperties(input);
    /// // {"a": {"b": 2, "c": 3}, "z": 1}
    /// </code>
    /// </example>
    public static string AlphabetizeProperties(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var node = JsonNode.Parse(json)
            ?? throw new JsonException("Failed to parse JSON input.");

        var sorted = SortNode(node);
        return sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
