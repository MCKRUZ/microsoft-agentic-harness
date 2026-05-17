using System.Text.Json;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Helper extensions for extracting values from <see cref="JsonElement"/> with fallback defaults.
/// Used during LLM output deserialization where fields may be absent or wrong-typed.
/// </summary>
internal static class JsonElementExtensions
{
    public static string GetPropertyOrDefault(this JsonElement el, string propertyName, string defaultValue)
    {
        return el.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? defaultValue
            : defaultValue;
    }
}
