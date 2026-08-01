using System.Text.Json;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Converts JSON tool arguments into the parameter dictionary <c>ITool.ExecuteAsync</c> accepts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Shared so the two paths into a tool cannot drift.</strong> An agent reaches a tool through
/// <see cref="AIToolConverter"/>, and an external caller reaches the same tool through
/// <c>IDirectToolInvoker</c>. Both start from JSON and both must arrive at the same CLR shapes, because
/// tools read their arguments by type — <c>FileSystemTool</c>, for one, matches <c>value is string</c>
/// and ignores anything else. Two independent conversions would eventually disagree about a number, a
/// null, or a nested object, and the symptom would be a tool that works for the agent and mysteriously
/// refuses the same arguments over HTTP.
/// </para>
/// <para>
/// Keys are matched case-insensitively, matching how tools look their parameters up.
/// </para>
/// </remarks>
public static class ToolParameters
{
    /// <summary>The key a non-JSON string payload is preserved under when it cannot be parsed.</summary>
    public const string RawInputKey = "raw_input";

    /// <summary>
    /// Parses tool parameters from a JSON element, accepting the three shapes a caller may produce.
    /// </summary>
    /// <param name="parametersJson">
    /// A JSON object (the common case), a JSON string containing an encoded object (which models
    /// frequently emit, and which is therefore double-decoded), or null/undefined.
    /// </param>
    /// <returns>
    /// The parameters. Never null: an absent, empty, or non-object payload yields an empty dictionary,
    /// because "no parameters" is valid for many operations and is not an error to report here.
    /// </returns>
    public static IReadOnlyDictionary<string, object?> FromJson(JsonElement? parametersJson)
    {
        if (parametersJson is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Empty();

        if (element.ValueKind == JsonValueKind.Object)
            return Flatten(element);

        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return Empty();

            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    ? Flatten(doc.RootElement)
                    : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [RawInputKey] = raw };
            }
            catch (JsonException)
            {
                // Preserved rather than discarded: a tool that accepts free text can still act on it,
                // and a tool that cannot will report an unrecognised parameter — which is a better
                // answer than silently receiving none.
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [RawInputKey] = raw };
            }
        }

        return Empty();
    }

    /// <summary>
    /// The shared answer for "no parameters". A fresh dictionary per call would allocate on the
    /// commonest path — several operations take no arguments at all — and the returned type is
    /// read-only, so one instance is safe to share.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, object?> Empty() => EmptyParameters;

    /// <summary>
    /// Projects one JSON object into CLR values: strings stay strings, integral numbers become
    /// <see cref="long"/> and the rest <see cref="double"/>, booleans and nulls map directly, and
    /// nested objects and arrays are preserved as their raw JSON text.
    /// </summary>
    /// <remarks>
    /// Nested structures are kept as text rather than recursed into because <c>ITool</c>'s parameter
    /// contract is flat. A tool expecting structured input parses that text itself, which keeps the
    /// decision about its shape with the tool that defined it.
    /// </remarks>
    private static Dictionary<string, object?> Flatten(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var i) ? i : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText()
            };
        }

        return dict;
    }
}
