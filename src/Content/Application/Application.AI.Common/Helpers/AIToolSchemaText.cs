using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Helpers;

/// <summary>
/// Flattens an <see cref="AITool"/>'s parameter schema to plain, decoded text — the property names
/// and string values a security scanner needs to inspect, distinct from the tool's description.
/// </summary>
/// <remarks>
/// Shared between <c>ScanningMcpToolProvider</c> (per-tool content scanning) and
/// <c>ToolChainBuilder</c> (surface-level scanning at merge time) so the two do not maintain separate
/// copies of the same decode logic and drift on it.
/// </remarks>
public static class AIToolSchemaText
{
    /// <summary>
    /// Returns the tool's parameter schema flattened to its <em>decoded</em> property names and
    /// string values, or <see langword="null"/> when the tool exposes none.
    /// </summary>
    /// <remarks>
    /// Decoding is the point, not a convenience. <c>JsonElement.ToString()</c> returns the raw JSON
    /// text with escape sequences intact, so a description containing a JSON-escaped invisible
    /// character reaches a scanner as the six literal characters of its escape sequence, and any rule
    /// that matches on the actual character never fires. A hostile server escaping its hidden
    /// characters would have walked straight past a check that only saw the raw text.
    /// </remarks>
    public static string? Extract(AITool tool)
    {
        if (tool is not AIFunction function || function.JsonSchema.ValueKind == JsonValueKind.Undefined)
            return null;

        var builder = new StringBuilder();
        AppendDecodedText(function.JsonSchema, builder);
        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// Appends every property name and string value in the element, decoded, separated by spaces.
    /// </summary>
    private static void AppendDecodedText(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    builder.Append(property.Name).Append(' ');
                    AppendDecodedText(property.Value, builder);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AppendDecodedText(item, builder);

                break;

            case JsonValueKind.String:
                builder.Append(element.GetString()).Append(' ');
                break;
        }
    }
}
