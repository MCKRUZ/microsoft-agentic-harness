using System.Text.Json;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Transforms the plain text a tool result carries, in whichever shape it reaches a policy boundary
/// in, without losing that shape.
/// </summary>
internal static class ToolResultText
{
    /// <summary>
    /// Runs <paramref name="result"/>'s text through <paramref name="transform"/> and returns it in the
    /// same shape it arrived: a raw <see langword="string"/> stays a string, and the JSON string
    /// element the function-invocation pipeline's own default marshaling serializes every genuine tool
    /// result into is re-serialized after transformation rather than unwrapped. A structured result
    /// (JSON object/array, or any other type) is returned unchanged — a text transform operates on free
    /// text, and rewriting the raw text of a structured value risks producing a malformed result the
    /// model then mis-parses.
    /// </summary>
    /// <remarks>
    /// Unwrapping a <see cref="JsonElement"/> into a bare string here would be a silent contract
    /// break: the model-facing chat client sends a raw string to the model verbatim, but
    /// JSON-serializes — and so re-quotes — a <c>JsonElement</c>. A caller that returned the
    /// transformed text as a bare string would change how the model reads quotes, newlines, and other
    /// characters in the result, not just scrub it.
    /// </remarks>
    public static object? TransformText(object? result, Func<string, string> transform) => result switch
    {
        string content => transform(content),
        JsonElement { ValueKind: JsonValueKind.String } element =>
            JsonSerializer.SerializeToElement(transform(element.GetString() ?? string.Empty)),
        _ => result
    };
}
