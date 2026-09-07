using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.AI.Caching;

namespace Infrastructure.AI.Caching;

/// <summary>
/// Pure transform that stamps an Anthropic prompt-cache breakpoint
/// (<c>cache_control: {"type": "ephemeral"}</c>) onto the system message of an
/// OpenAI-format chat-completions request body.
/// </summary>
/// <remarks>
/// <para>
/// Anthropic prompt caching is opt-in: a request must mark which prefix to cache. When the harness
/// talks to Claude through an OpenAI-compatible gateway (e.g. OpenRouter), the breakpoint is a
/// <c>cache_control</c> field on a content block inside the <c>messages</c> array. This transform
/// places a single breakpoint on the <em>last</em> system message — the large, stable prefix
/// (system prompt, and any tool preamble folded into the system block) that repeats every turn.
/// Everything up to and including that block becomes cache-eligible.
/// </para>
/// <para>
/// The transform is defensive by construction: any body it does not recognise (malformed JSON, no
/// <c>messages</c> array, no system message, an unexpected content shape) is returned byte-for-byte
/// unchanged, so it can sit on the live request path without risk of corrupting a request. It is
/// also idempotent — a body that already carries a breakpoint is left as-is.
/// </para>
/// <para>
/// Caching only takes effect when the cached prefix exceeds the provider's minimum size (~1024
/// tokens for Sonnet/Opus) and the prefix is byte-identical across turns; below that, the provider
/// silently ignores the breakpoint, so stamping it unconditionally is safe.
/// </para>
/// <para>
/// <strong>Marker-based override.</strong> The "mark whatever is last" rule above is correct only
/// when nothing after the stable prefix is expected to change. Once a caller attaches a genuinely
/// per-turn block after it (see <c>CallerTurnContextProvider</c>), marking "last" would mark the
/// <em>changing</em> content, and the provider would never observe a byte-identical prefix — silent,
/// permanent cache misses. A caller that needs the boundary placed deliberately terminates its
/// stable content with <see cref="CacheBoundaryMarker"/>; when present, this transform splits that
/// system message's text at the marker (marker text removed from the output) and marks only the
/// piece before it, ignoring the "last message" scan entirely. Absent the marker — every existing
/// caller — behavior is byte-for-byte what it was before this override existed.
/// </para>
/// </remarks>
public static class PromptCacheInjector
{
    private const string MessagesProperty = "messages";
    private const string RoleProperty = "role";
    private const string ContentProperty = "content";
    private const string CacheControlProperty = "cache_control";
    private const string SystemRole = "system";

    /// <summary>
    /// The boundary sentinel — see <see cref="PromptCacheConventions.CacheBoundaryMarker"/> for the
    /// full contract. Defined in Domain so both this transform and the instruction-building code in
    /// Application.AI.Common (which cannot depend on this Infrastructure project) agree on the exact
    /// string.
    /// </summary>
    public const string CacheBoundaryMarker = PromptCacheConventions.CacheBoundaryMarker;

    /// <summary>
    /// Returns <paramref name="requestJson"/> with a <c>cache_control: ephemeral</c> breakpoint
    /// placed per the rules above, or the original string unchanged when no safe injection applies.
    /// </summary>
    /// <param name="requestJson">The serialized OpenAI chat-completions request body.</param>
    /// <returns>The rewritten body, or <paramref name="requestJson"/> unchanged.</returns>
    public static string InjectSystemCacheControl(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return requestJson;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestJson);
        }
        catch (JsonException)
        {
            return requestJson;
        }

        if (root is not JsonObject body || body[MessagesProperty] is not JsonArray messages)
            return requestJson;

        var markedMessage = FindMarkedSystemMessage(messages);
        if (markedMessage is not null)
            return TrySplitAtMarker(markedMessage) ? body.ToJsonString() : requestJson;

        var systemMessage = FindLastSystemMessage(messages);
        if (systemMessage is null)
            return requestJson;

        return TryMark(systemMessage) ? body.ToJsonString() : requestJson;
    }

    /// <summary>
    /// Finds the system message whose plain-string content contains <see cref="CacheBoundaryMarker"/>,
    /// or <see langword="null"/> when no system message carries one — including when a system
    /// message's content is already an array (the marker is only ever appended to the plain-string
    /// static instruction, never to array-shaped content), which correctly falls through to the
    /// existing "last message" behavior for that case.
    /// </summary>
    private static JsonObject? FindMarkedSystemMessage(JsonArray messages)
    {
        foreach (var node in messages)
        {
            if (node is JsonObject message
                && message[RoleProperty]?.GetValue<string>() == SystemRole
                && message[ContentProperty] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && text.Contains(CacheBoundaryMarker, StringComparison.Ordinal))
            {
                return message;
            }
        }

        return null;
    }

    /// <summary>
    /// Splits a marked system message's string content at <see cref="CacheBoundaryMarker"/> into two
    /// text blocks — the stable prefix (marked cacheable) and whatever a caller appended after it
    /// (left unmarked, so it is sent fresh every turn without ever being the cached content). Returns
    /// <see langword="false"/> only if the content shape changed between detection and this call
    /// (defensive; cannot happen via the single call site above).
    /// </summary>
    private static bool TrySplitAtMarker(JsonObject markedMessage)
    {
        if (markedMessage[ContentProperty] is not JsonValue value || !value.TryGetValue<string>(out var text))
            return false;

        var markerIndex = text.IndexOf(CacheBoundaryMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        var stable = text[..markerIndex];
        var rest = text[(markerIndex + CacheBoundaryMarker.Length)..];

        var blocks = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = stable,
                [CacheControlProperty] = Ephemeral(),
            },
        };

        if (rest.Length > 0)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = rest,
            });
        }

        markedMessage[ContentProperty] = blocks;
        return true;
    }

    private static JsonObject? FindLastSystemMessage(JsonArray messages)
    {
        JsonObject? found = null;
        foreach (var node in messages)
        {
            if (node is JsonObject message
                && message[RoleProperty]?.GetValue<string>() == SystemRole)
            {
                found = message;
            }
        }

        return found;
    }

    /// <summary>Marks the system message in place. Returns false when the content shape is unsupported.</summary>
    private static bool TryMark(JsonObject systemMessage)
    {
        var content = systemMessage[ContentProperty];

        // Plain string content → convert to a single cached text block.
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            systemMessage[ContentProperty] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    [CacheControlProperty] = Ephemeral()
                }
            };
            return true;
        }

        // Array content → mark the last block (idempotent).
        if (content is JsonArray parts && parts.Count > 0 && parts[^1] is JsonObject lastBlock)
        {
            if (lastBlock[CacheControlProperty] is null)
                lastBlock[CacheControlProperty] = Ephemeral();
            return true;
        }

        return false;
    }

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };
}
