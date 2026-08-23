using System.Text.Json;
using System.Text.Json.Nodes;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Redaction;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Transforms the plain text a tool result carries, in whichever shape it reaches a policy boundary
/// in, without losing that shape.
/// </summary>
/// <remarks>
/// <para>
/// Unwrapping a <see cref="JsonElement"/> into a bare string here would be a silent contract break: the
/// model-facing chat client sends a raw string to the model verbatim, but JSON-serializes — and so
/// re-quotes — a <c>JsonElement</c>. Returning transformed text in the wrong shape would change how the
/// model reads quotes, newlines, and other characters in the result, not just scrub it.
/// </para>
/// <para>
/// Handles five shapes a tool result can arrive in: a raw <see langword="string"/> and a serialized
/// JSON string element cover a keyed-DI/skill (<c>ITool</c>-backed) success. An MCP tool's success
/// reaches this boundary differently — <c>McpClientTool.InvokeCoreAsync</c> returns a bare
/// <see cref="TextContent"/> for a single-content-block result and an <see cref="AIContent"/> array for
/// a multi-block one, falling back to a serialized <c>CallToolResult</c> (a structured
/// <see cref="JsonElement"/> carrying its own <c>content</c> array) only when the result carries
/// structured content or protocol metadata — see #483, which closed that fifth shape.
/// </para>
/// </remarks>
internal static class ToolResultText
{
    /// <summary>
    /// Substituted when a sanitizer reports it changed something but returns no text to show for it — a
    /// runtime contract break <see cref="ICompositeResponseSanitizer"/> doesn't enforce against a
    /// consumer-supplied implementation. Every caller of <see cref="Sanitize"/> relies on a must-not-throw
    /// contract (see <c>GovernedAIFunction</c>'s and <c>DirectToolInvoker</c>'s own remarks); degrading to
    /// a visible placeholder here, the same way <c>ReportedFailureText</c> does for its own sanitizer
    /// dependency, keeps that contract rather than propagating an exception out of nearly every tool call
    /// this fix now touches.
    /// </summary>
    private const string CorruptedSanitizerOutputPlaceholder =
        "[tool result withheld: the response sanitizer returned no content]";

    /// <summary>
    /// Runs <paramref name="result"/>'s text through <paramref name="sanitizer"/> and returns it in the
    /// same shape it arrived — unless nothing changed, in which case <paramref name="result"/> is
    /// returned untouched rather than paying for a reconstruction that would only reproduce an
    /// equivalent value. A structured or unrecognized result is returned unchanged: a sanitizer operates
    /// on free text, and rewriting the raw text of a structured value risks producing a malformed result
    /// the model then mis-parses.
    /// </summary>
    public static object? Sanitize(object? result, ICompositeResponseSanitizer sanitizer, string toolName) =>
        Transform(result, text => SanitizeText(text, sanitizer, toolName));

    /// <summary>
    /// Runs <paramref name="result"/>'s text through <paramref name="sanitizer"/> and then
    /// <paramref name="redactionFilter"/>, in that order, preserving shape exactly as <see cref="Sanitize"/>
    /// does. Used only by <see cref="DefaultToolClassificationGate.RedactResult"/> — the path a
    /// classification policy's <c>Redact</c> verdict takes, which must do strictly more than the baseline
    /// sanitize every other tool result already gets (#484), not the same thing under a different name.
    /// </summary>
    /// <remarks>
    /// Sanitize before redact, mirroring <see cref="Tools.ReportedFailureText.PrepareForReporting"/>'s
    /// own ordering rationale: an injection payload is stripped before the (now shorter, already-inert)
    /// text is scanned for secret patterns, rather than redacting first and handing the sanitizer text
    /// that may already contain <c>[REDACTED:...]</c> placeholders to no benefit.
    /// </remarks>
    public static object? SanitizeAndRedact(
        object? result,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        string toolName) =>
        Transform(result, text => redactionFilter.Redact(SanitizeText(text, sanitizer, toolName), RedactionCategories.All));

    /// <summary>
    /// Applies <paramref name="transform"/> to the free text carried by <paramref name="result"/>,
    /// preserving whichever of the five recognized shapes it arrived in, and returns
    /// <paramref name="result"/> itself — not a reconstructed equivalent — whenever the transform left
    /// every text value unchanged.
    /// </summary>
    private static object? Transform(object? result, Func<string, string> transform)
    {
        switch (result)
        {
            case string content:
            {
                var transformed = transform(content);
                return string.Equals(transformed, content, StringComparison.Ordinal) ? result : transformed;
            }
            case JsonElement { ValueKind: JsonValueKind.String } element:
            {
                var original = element.GetString() ?? string.Empty;
                var transformed = transform(original);
                return string.Equals(transformed, original, StringComparison.Ordinal)
                    ? result
                    : JsonSerializer.SerializeToElement(transformed);
            }
            // A single-content-block MCP tool success reaches this boundary as a bare TextContent, not a
            // JsonElement — McpClientTool.InvokeCoreAsync only falls back to serializing the whole
            // CallToolResult when structured content or protocol metadata is present.
            case TextContent text:
            {
                var transformed = transform(text.Text);
                return string.Equals(transformed, text.Text, StringComparison.Ordinal)
                    ? result
                    : WithText(text, transformed);
            }
            // A multi-content-block MCP tool success reaches this boundary as AIContent[]. Only
            // TextContent elements carry free text to transform; anything else (DataContent — images,
            // files) passes through untouched.
            case AIContent[] blocks:
            {
                AIContent[]? transformedBlocks = null;
                for (var i = 0; i < blocks.Length; i++)
                {
                    if (blocks[i] is not TextContent block)
                        continue;

                    var transformed = transform(block.Text);
                    if (string.Equals(transformed, block.Text, StringComparison.Ordinal))
                        continue;

                    transformedBlocks ??= (AIContent[])blocks.Clone();
                    transformedBlocks[i] = WithText(block, transformed);
                }
                return transformedBlocks ?? result;
            }
            // #483: an MCP tool success carrying structuredContent or protocol _meta serializes as the
            // whole CallToolResult rather than a bare TextContent/AIContent[] — but it still carries the
            // same content array of text/data blocks, one JSON level down. Detected structurally (a
            // top-level "content" array) rather than by referencing the MCP protocol's own CLR types:
            // this project deliberately has no dependency on ModelContextProtocol.Core (see the type
            // remarks), so the shape is recognized by what it looks like, not by decoding it as a
            // specific SDK type.
            case JsonElement { ValueKind: JsonValueKind.Object } element when TryGetContentArray(element, out var content):
            {
                var transformed = TransformSerializedContentBlocks(element, content, transform);
                return transformed ?? result;
            }
            default:
                return result;
        }
    }

    private static bool TryGetContentArray(JsonElement element, out JsonElement content) =>
        element.TryGetProperty("content", out content) && content.ValueKind == JsonValueKind.Array;

    /// <summary>
    /// Walks the <c>content</c> array of a serialized <c>CallToolResult</c>, applying
    /// <paramref name="transform"/> to every <c>{"type":"text","text":"..."}</c> block's text. Every
    /// other property (<c>isError</c>, <c>structuredContent</c>, <c>_meta</c>, non-text content blocks)
    /// is carried through unchanged. Returns <see langword="null"/> when no text block's content
    /// changed, so the caller can keep the original <see cref="JsonElement"/> instead of an equivalent
    /// reconstruction.
    /// </summary>
    private static JsonElement? TransformSerializedContentBlocks(
        JsonElement original, JsonElement content, Func<string, string> transform)
    {
        JsonNode? root = null;
        var index = 0;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.String
                && typeProp.GetString() == "text"
                && block.TryGetProperty("text", out var textProp)
                && textProp.ValueKind == JsonValueKind.String)
            {
                var text = textProp.GetString() ?? string.Empty;
                var transformed = transform(text);
                if (!string.Equals(transformed, text, StringComparison.Ordinal))
                {
                    // Parsed lazily, only once a block actually needs rewriting: the common case (a
                    // structured result with nothing to scrub) pays no JsonNode allocation at all.
                    root ??= JsonNode.Parse(original.GetRawText());
                    root!["content"]![index]!["text"] = transformed;
                }
            }

            index++;
        }

        return root is null ? null : JsonSerializer.SerializeToElement(root);
    }

    private static string SanitizeText(string text, ICompositeResponseSanitizer sanitizer, string toolName)
    {
        var scrubbed = sanitizer.Sanitize(text, toolName);
        return scrubbed.WasSanitized ? SanitizedText(scrubbed) : text;
    }

    private static string SanitizedText(SanitizationResult result) =>
        result.SanitizedContent ?? CorruptedSanitizerOutputPlaceholder;

    private static TextContent WithText(TextContent original, string text) => new(text)
    {
        Annotations = original.Annotations,
        RawRepresentation = original.RawRepresentation,
        AdditionalProperties = original.AdditionalProperties
    };
}
