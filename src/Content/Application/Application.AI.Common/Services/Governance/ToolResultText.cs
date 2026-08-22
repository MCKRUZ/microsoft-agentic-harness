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
/// <see cref="JsonElement"/>) only when the result carries structured content or protocol metadata. The
/// first two shapes are handled here; a result that falls back to the serialized-<c>CallToolResult</c>
/// shape is not yet — tracked separately, since sanitizing embedded text inside an arbitrary nested JSON
/// structure needs its own design rather than a fourth case bolted onto this switch.
/// </para>
/// </remarks>
internal static class ToolResultText
{
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
                return scrubbed.WasSanitized ? RequireText(scrubbed, toolName) : result;
            }
            case JsonElement { ValueKind: JsonValueKind.String } element:
            {
                var scrubbed = sanitizer.Sanitize(element.GetString() ?? string.Empty, toolName);
                return scrubbed.WasSanitized
                    ? JsonSerializer.SerializeToElement(RequireText(scrubbed, toolName))
                    : result;
            }
            // A single-content-block MCP tool success reaches this boundary as a bare TextContent, not a
            // JsonElement — McpClientTool.InvokeCoreAsync only falls back to serializing the whole
            // CallToolResult when structured content or protocol metadata is present.
            case TextContent text:
            {
                var scrubbed = sanitizer.Sanitize(text.Text, toolName);
                return scrubbed.WasSanitized ? WithText(text, RequireText(scrubbed, toolName)) : result;
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
                    sanitizedBlocks[i] = WithText(block, RequireText(scrubbed, toolName));
                }
                return sanitizedBlocks ?? result;
            }
            default:
                return result;
        }
    }

    /// <summary>
    /// Fails loudly rather than silently emptying a tool result: <see cref="SanitizationResult.SanitizedContent"/>
    /// is non-nullable by contract, but that contract isn't enforced at runtime against a
    /// consumer-supplied <see cref="ICompositeResponseSanitizer"/>, and a null here would otherwise reach
    /// the model as a bare JSON <see langword="null"/> or an empty result with no signal of why.
    /// </summary>
    private static string RequireText(SanitizationResult result, string toolName) =>
        result.SanitizedContent
        ?? throw new InvalidOperationException(
            $"The response sanitizer returned null sanitized content for tool '{toolName}'.");

    private static TextContent WithText(TextContent original, string text) => new(text)
    {
        Annotations = original.Annotations,
        RawRepresentation = original.RawRepresentation,
        AdditionalProperties = original.AdditionalProperties
    };
}
