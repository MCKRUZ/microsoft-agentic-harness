using System.Text.Json;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Sanitizes the plain text a tool result carries, in whichever shape it reaches a policy boundary in,
/// without losing that shape.
/// </summary>
/// <remarks>
/// <para>
/// Unwrapping a <see cref="JsonElement"/> into a bare string here would be a silent contract break: the
/// model-facing chat client sends a raw string to the model verbatim, but JSON-serializes — and so
/// re-quotes — a <c>JsonElement</c>. Returning sanitized text in the wrong shape would change how the
/// model reads quotes, newlines, and other characters in the result, not just scrub it.
/// </para>
/// <para>
/// Handles four shapes a tool result can arrive in, not just two: a raw <see langword="string"/> and a
/// serialized JSON string element cover a keyed-DI/skill (<c>ITool</c>-backed) success. An MCP tool's
/// success reaches this boundary differently — <c>McpClientTool.InvokeCoreAsync</c> returns a bare
/// <see cref="TextContent"/> for a single-content-block result and an <see cref="AIContent"/> array for
/// a multi-block one, falling back to a serialized <c>CallToolResult</c> (a structured
/// <see cref="JsonElement"/>) only when the result carries structured content or protocol metadata.
/// </para>
/// <para>
/// <strong>The serialized-<c>CallToolResult</c> shape is not sanitized — a known, tracked gap, not an
/// oversight.</strong> Handling it means walking the embedded <c>content</c> array inside an arbitrary
/// nested JSON structure, and this type deliberately has no dependency on the MCP protocol's own CLR
/// types (<c>Application.AI.Common</c> doesn't reference <c>ModelContextProtocol.Core</c> — that
/// knowledge belongs to <c>Infrastructure.AI.MCP</c>, not here), so closing it needs its own design
/// rather than a generic-JSON special case bolted onto this switch. See the tracking issue for the
/// current thinking on where that logic should actually live.
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
    /// same shape it arrived — unless sanitizing found nothing to change, in which case
    /// <paramref name="result"/> is returned untouched rather than paying for a reconstruction that
    /// would only reproduce an equivalent value. A structured or unrecognized result is returned
    /// unchanged: the sanitizer operates on free text, and rewriting the raw text of a structured value
    /// risks producing a malformed result the model then mis-parses.
    /// </summary>
    public static object? Sanitize(object? result, ICompositeResponseSanitizer sanitizer, string toolName)
    {
        switch (result)
        {
            case string content:
            {
                var scrubbed = sanitizer.Sanitize(content, toolName);
                return scrubbed.WasSanitized ? SanitizedText(scrubbed) : result;
            }
            case JsonElement { ValueKind: JsonValueKind.String } element:
            {
                var scrubbed = sanitizer.Sanitize(element.GetString() ?? string.Empty, toolName);
                return scrubbed.WasSanitized
                    ? JsonSerializer.SerializeToElement(SanitizedText(scrubbed))
                    : result;
            }
            // A single-content-block MCP tool success reaches this boundary as a bare TextContent, not a
            // JsonElement — McpClientTool.InvokeCoreAsync only falls back to serializing the whole
            // CallToolResult when structured content or protocol metadata is present.
            case TextContent text:
            {
                var scrubbed = sanitizer.Sanitize(text.Text, toolName);
                return scrubbed.WasSanitized ? WithText(text, SanitizedText(scrubbed)) : result;
            }
            // A multi-content-block MCP tool success reaches this boundary as AIContent[]. Only
            // TextContent elements carry free text to sanitize; anything else (DataContent — images,
            // files) passes through untouched.
            case AIContent[] blocks:
            {
                AIContent[]? sanitizedBlocks = null;
                for (var i = 0; i < blocks.Length; i++)
                {
                    if (blocks[i] is not TextContent block)
                        continue;

                    var scrubbed = sanitizer.Sanitize(block.Text, toolName);
                    if (!scrubbed.WasSanitized)
                        continue;

                    sanitizedBlocks ??= (AIContent[])blocks.Clone();
                    sanitizedBlocks[i] = WithText(block, SanitizedText(scrubbed));
                }
                return sanitizedBlocks ?? result;
            }
            default:
                return result;
        }
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
