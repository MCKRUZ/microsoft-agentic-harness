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
    /// Coerces an optional integer parameter, accepting the three CLR shapes a tool argument can
    /// actually arrive as (#575).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this exists.</strong> <see cref="Flatten"/> boxes every JSON integral number as
    /// <see langword="long"/>, so a model-supplied integer argument is a <see langword="long"/> in
    /// practice, not an <see langword="int"/> — but a tool's own parameter is naturally an
    /// <see langword="int"/> or <see langword="int"/>?. Two independent, near-identical coercion
    /// switches existed before this (<c>ToolResultFetchTool.TryGetOffset</c>,
    /// <c>DocumentSearchTool.GetOptionalInt</c>) and were not equivalent: one range-checked a
    /// <see langword="long"/> before narrowing it, the other cast unconditionally
    /// (<c>(int)someLong</c>), silently wrapping an out-of-range value into an unrelated, possibly
    /// negative <see langword="int"/> instead of refusing it.
    /// </para>
    /// <para>
    /// <c>true</c> with <paramref name="value"/> <see langword="null"/> means "absent — use your own
    /// default." <c>false</c> means "present, but not a well-formed integer within
    /// [<paramref name="min"/>, <paramref name="max"/>]" — a caller should refuse the request outright
    /// rather than silently substituting a default, the same distinction
    /// <c>ToolResultFetchTool.TryGetOffset</c> already drew for <c>offset</c>: a caller that got an
    /// argument wrong should be told so, not have its request silently proceed as if it had said
    /// nothing.
    /// </para>
    /// </remarks>
    /// <param name="parameters">The tool's parameter dictionary.</param>
    /// <param name="key">The parameter name to look up.</param>
    /// <param name="value">
    /// The parsed value, or <see langword="null"/> when <paramref name="key"/> is absent or explicitly
    /// null. Also <see langword="null"/> when this method returns <see langword="false"/>.
    /// </param>
    /// <param name="min">The smallest accepted value, inclusive. Defaults to <see cref="int.MinValue"/>.</param>
    /// <param name="max">The largest accepted value, inclusive. Defaults to <see cref="int.MaxValue"/>.</param>
    /// <returns>
    /// <see langword="false"/> when <paramref name="key"/> is present but is neither an
    /// <see langword="int"/>, a <see langword="long"/>, nor a numeric string — or is any of those but
    /// outside [<paramref name="min"/>, <paramref name="max"/>]. <see langword="true"/> otherwise.
    /// </returns>
    public static bool TryGetOptionalInt(
        IReadOnlyDictionary<string, object?> parameters,
        string key,
        out int? value,
        int min = int.MinValue,
        int max = int.MaxValue)
    {
        if (!parameters.TryGetValue(key, out var raw) || raw is null)
        {
            value = null;
            return true;
        }

        var parsed = raw switch
        {
            int i => i,
            long l and >= int.MinValue and <= int.MaxValue => (int)l,
            string s when int.TryParse(s, out var fromString) => fromString,
            _ => (int?)null
        };

        if (parsed is not { } parsedValue || parsedValue < min || parsedValue > max)
        {
            value = null;
            return false;
        }

        value = parsedValue;
        return true;
    }

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
    /// commonest path, since several operations take no arguments at all.
    /// </summary>
    /// <remarks>
    /// A genuinely immutable instance, not a <c>Dictionary</c> behind a read-only interface. Sharing a
    /// mutable one would let any consumer that downcast its parameter map corrupt the value every
    /// other call receives — a trivial mistake to make, and one whose blast radius is now the external
    /// HTTP surface as well as the agent path.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>.Empty;

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

                // The (object?) cast is load-bearing and must not be "tidied away". Without it the
                // conditional has a natural type of double — long converts to double implicitly, so
                // that is the common type — and every whole number boxes as a double. Tools match
                // their arguments by type (DocumentSearchTool accepts int/long/string and returns
                // null for anything else), so the symptom is not an error: the parameter is silently
                // discarded and the tool quietly uses its default.
                JsonValueKind.Number => prop.Value.TryGetInt64(out var i) ? (object?)i : prop.Value.GetDouble(),

                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText()
            };
        }

        return dict;
    }
}
