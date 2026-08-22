using System.Text.Json;
using Application.AI.Common.Interfaces.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Sanitizes the plain text a tool result carries, in whichever shape it reaches a policy boundary in,
/// without losing that shape.
/// </summary>
internal static class ToolResultText
{
    /// <summary>
    /// Runs <paramref name="result"/>'s text through <paramref name="sanitizer"/> and returns it in the
    /// same shape it arrived: a raw <see langword="string"/> stays a string, and the JSON string
    /// element the function-invocation pipeline's own default marshaling serializes every genuine tool
    /// result into is re-serialized after sanitizing — unless sanitizing found nothing to change, in
    /// which case <paramref name="result"/> is returned untouched rather than paying for a round trip
    /// that would only reconstruct a byte-identical element. A structured result (JSON object/array, or
    /// any other type) is returned unchanged: the sanitizer operates on free text, and rewriting the raw
    /// text of a structured value risks producing a malformed result the model then mis-parses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unwrapping a <see cref="JsonElement"/> into a bare string here would be a silent contract break:
    /// the model-facing chat client sends a raw string to the model verbatim, but JSON-serializes — and
    /// so re-quotes — a <c>JsonElement</c>. Returning the sanitized text as a bare string would change
    /// how the model reads quotes, newlines, and other characters in the result, not just scrub it.
    /// </para>
    /// <para>
    /// Deliberately no length cap on the scan, unlike <c>ReportedFailureText</c>'s: that type withholds
    /// oversized input behind a placeholder, which is safe only because a failure message doesn't need
    /// to survive intact. A tool's raw output does — it can legitimately be large (a file read, an HTTP
    /// fetch), and either truncating it or skipping the sanitize pass above a size threshold would let an
    /// attacker-controlled payload padded past that threshold reach the model unscanned, reopening
    /// exactly the gap this type exists to close. The sanitizer's own regex patterns already carry a
    /// per-pattern match timeout (see <c>DefaultContentRedactionFilter.MatchTimeout</c>), which bounds
    /// worst-case cost without trading away coverage.
    /// </para>
    /// </remarks>
    public static object? Sanitize(object? result, ICompositeResponseSanitizer sanitizer, string toolName)
    {
        var text = result switch
        {
            string content => content,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            _ => null
        };

        if (text is null)
            return result;

        var sanitized = sanitizer.Sanitize(text, toolName).SanitizedContent;
        if (ReferenceEquals(sanitized, text))
            return result;

        return result is string ? sanitized : JsonSerializer.SerializeToElement(sanitized);
    }
}
