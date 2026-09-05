using Application.AI.Common.Services.Governance;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Wraps an MCP-provided <see cref="AIFunction"/> so a non-throwing protocol-level failure is
/// converted into the same <see cref="ConvertedToolFailure"/> marker <c>AIToolConverter</c> already
/// produces for <c>ITool</c>-backed tools — normalizing failure detection at the point an MCP tool
/// becomes an <see cref="AIFunction"/>, instead of leaving <see cref="GovernedAIFunction"/> to sniff
/// the wire shape itself gated by a caller-supplied provenance flag (see #468).
/// </summary>
/// <remarks>
/// <para>
/// Confirmed against the MCP C# SDK's <c>McpClientTool.InvokeCoreAsync</c> source: a tool call whose
/// <c>CallToolResult.IsError</c> is <see langword="true"/> returns normally —
/// <c>JsonSerializer.SerializeToElement(result, ...)</c> — it never throws. This wrapper recognizes
/// that shape by structure (<c>isError</c> + <c>content</c>, the MCP wire shape) and converts it to
/// <see cref="ConvertedToolFailure"/> before <see cref="GovernedAIFunction"/> (or anything else) ever
/// sees the result, so every downstream consumer checks exactly one failure shape regardless of tool
/// source. Only apply this wrapper to an <see cref="AIFunction"/> actually resolved from an MCP
/// server — the shape check is structural, not provenance-based, and a non-MCP tool's genuine success
/// happening to use the same field names for unrelated business reasons must never be wrapped by this
/// class in the first place (the caller, not this type, is what guarantees that).
/// </para>
/// <para>
/// No <c>MarshalResult</c> trick is needed to keep <see cref="ConvertedToolFailure"/>'s CLR identity
/// intact here, unlike <c>AIToolConverter</c>: this is a plain <see cref="DelegatingAIFunction"/>
/// subclass (like <see cref="GovernedAIFunction"/> itself), not an <c>AIFunctionFactory</c>-created
/// function — there is no automatic JSON re-serialization step between this override returning and
/// the next decorator seeing the value.
/// </para>
/// </remarks>
internal sealed class McpFailureNormalizingAIFunction : DelegatingAIFunction
{
    /// <param name="innerFunction">The MCP-provided tool function to normalize failures for.</param>
    public McpFailureNormalizingAIFunction(AIFunction innerFunction) : base(innerFunction)
    {
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        var failureText = TryGetMcpFailureText(result);
        return failureText is null ? result : new ConvertedToolFailure(failureText);
    }

    /// <summary>
    /// Recognizes an MCP tool failure by the shape the protocol actually puts on the wire — a JSON
    /// object with <c>isError: true</c> and a <c>content</c> array of text blocks. Returns
    /// <see langword="null"/> for anything that isn't that shape, including a genuine success (which
    /// reaches here as a <see cref="JsonElement"/> too, just without <c>isError: true</c>).
    /// </summary>
    /// <remarks>
    /// The inner loop gates each block on <see cref="ToolResultText.IsContentBlock"/> — the same
    /// predicate <see cref="ToolResultText.TryGetContentArray"/> already gates the outer
    /// <c>content</c>-array recognition on — so this stops being a fourth independent re-derivation of
    /// "what counts as a content block" (#554; <c>ToolResultText.cs</c> itself had three before #488).
    /// This does not recover a message from a non-protocol-legal block that carries a top-level
    /// <c>text</c> property with no <c>type</c> discriminator (e.g. <c>{"text":"disk full"}</c>) — a
    /// real MCP content block always carries <c>type</c>, and <c>TryGetContentArray</c>'s own gate
    /// (unchanged here) already rejects an array whose only block lacks one before this loop runs.
    /// </remarks>
    private static string? TryGetMcpFailureText(object? result)
    {
        if (result is not JsonElement { ValueKind: JsonValueKind.Object } element)
            return null;

        if (!element.TryGetProperty("isError", out var isError) || isError.ValueKind != JsonValueKind.True)
            return null;

        if (ToolResultText.TryGetContentArray(element, out var content))
        {
            foreach (var block in content.EnumerateArray())
            {
                if (ToolResultText.IsContentBlock(block, out _)
                    && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString();
            }
        }

        return "MCP tool reported failure with no message.";
    }
}
