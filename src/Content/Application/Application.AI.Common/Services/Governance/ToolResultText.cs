using System.Text.Json;
using Application.AI.Common.Interfaces.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Sanitizes the plain text a tool result carries, in whichever shape it reaches a policy boundary in,
/// without losing that shape.
/// </summary>
internal static class ToolResultText
{
    // Mirrors ReportedFailureText.MaxScanLength: bounds worst-case regex-scan cost on a
    // remotely-triggered, attacker-controlled tool result before the sanitizer's pattern chain runs.
    // Unlike a failure message, a tool's raw output can legitimately be arbitrarily large (a file read,
    // an HTTP fetch), so this path needs its own cap rather than inheriting one from an upstream caller.
    private const int MaxScanLength = 64 * 1024;

    /// <summary>
    /// Runs <paramref name="result"/>'s text through <paramref name="sanitizer"/> and returns it in the
    /// same shape it arrived: a raw <see langword="string"/> stays a string, and the JSON string
    /// element the function-invocation pipeline's own default marshaling serializes every genuine tool
    /// result into is re-serialized after sanitizing — unless sanitizing found nothing to change, in
    /// which case <paramref name="result"/> is returned untouched rather than paying for a round trip
    /// that would only reconstruct a byte-identical element. A structured result (JSON object/array, or
    /// any other type) is returned unchanged: the sanitizer operates on free text, and rewriting the raw
    /// text of a structured value risks producing a malformed result the model then mis-parses. Text
    /// longer than <see cref="MaxScanLength"/> is left unsanitized rather than scanned, for the same
    /// reason <c>ReportedFailureText</c> bounds its own input before its sanitizer runs.
    /// </summary>
    /// <remarks>
    /// Unwrapping a <see cref="JsonElement"/> into a bare string here would be a silent contract break:
    /// the model-facing chat client sends a raw string to the model verbatim, but JSON-serializes — and
    /// so re-quotes — a <c>JsonElement</c>. Returning the sanitized text as a bare string would change
    /// how the model reads quotes, newlines, and other characters in the result, not just scrub it.
    /// </remarks>
    public static object? Sanitize(object? result, ICompositeResponseSanitizer sanitizer, string toolName)
    {
        var text = result switch
        {
            string content => content,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            _ => null
        };

        if (text is null || text.Length > MaxScanLength)
            return result;

        var sanitized = sanitizer.Sanitize(text, toolName).SanitizedContent;
        if (ReferenceEquals(sanitized, text))
            return result;

        return result is string ? sanitized : JsonSerializer.SerializeToElement(sanitized);
    }
}
