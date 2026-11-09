using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Presentation.FoundryHost;

/// <summary>
/// Reads every piece of caller-written text out of what a Foundry <c>/responses</c> request hands the agent, so it can
/// be screened before the model sees it.
/// </summary>
/// <remarks>
/// <para>
/// On this host the caller writes every message in a request, whatever its role: an assistant turn, a tool call or a
/// tool result in the request's input is caller text, not the agent's own output. So nothing is skipped by role.
/// </para>
/// <para>
/// Text, reasoning text and a string tool result are read as they are. From any other content every string in its JSON
/// form is read, unescaped, so a string inside it (tool-call arguments, a structured tool result) is screened too. Files, images and links are
/// flagged instead: the screening reads text only, and content it cannot read is refused rather than passed.
/// </para>
/// </remarks>
internal static class FoundryCallerText
{
    /// <summary>The caller text read from a request, and whether it carried content the screening cannot read.</summary>
    /// <param name="Text">The caller's text, one piece per paragraph; empty when there is none, and not read at all when
    /// the request carried content the screening cannot read, since such a request is refused whatever its text.</param>
    /// <param name="HasUnscreenableContent">True when a message carried a file, an image or a link.</param>
    internal sealed record Reading(string Text, bool HasUnscreenableContent);

    /// <summary>Reads the caller text of <paramref name="instructions"/> and every message in <paramref name="messages"/>.</summary>
    /// <param name="messages">Every message the agent will receive this run, whatever its role.</param>
    /// <param name="instructions">The request's own instructions, or null when it set none.</param>
    internal static Reading Read(IEnumerable<ChatMessage> messages, string? instructions)
    {
        var contents = messages.SelectMany(m => m.Contents).ToList();
        if (contents.Any(c => c is DataContent or UriContent or HostedFileContent))
            return new Reading(string.Empty, HasUnscreenableContent: true);

        var text = string.Join(
            "\n\n",
            contents.Select(TextOf).Prepend(instructions).Where(piece => !string.IsNullOrWhiteSpace(piece)));
        return new Reading(text, HasUnscreenableContent: false);
    }

    private static string? TextOf(AIContent content) => content switch
    {
        TextContent text => text.Text,
        TextReasoningContent reasoning => reasoning.Text,
        FunctionResultContent { Result: string result } => result,
        _ => string.Join("\n", StringsIn(JsonSerializer.SerializeToNode(content, AIJsonUtilities.DefaultOptions))),
    };

    // The strings themselves, not the serialized JSON: escaping turns a line break into a backslash and an "n", and
    // the injection scan must read the text the model will read.
    private static IEnumerable<string> StringsIn(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(property => StringsIn(property.Value).Prepend(property.Key)),
        JsonArray array => array.SelectMany(StringsIn),
        JsonValue value when value.TryGetValue(out string? text) => [text],
        _ => [],
    };
}
