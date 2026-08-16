using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces;
using Domain.Common.Config.MetaHarness;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Security;

/// <summary>
/// Regex-based secret redactor that scans free-text strings for known secret patterns
/// and filters config keys by a configurable denylist.
/// </summary>
/// <remarks>
/// <para>
/// Patterns are compiled once at construction time from <see cref="MetaHarnessConfig.SecretsRedactionPatterns"/>
/// and the hardcoded free-text regex set. Config changes are not reflected at runtime — restart
/// the service to pick up updated denylist patterns.
/// </para>
/// <para>
/// All methods are thread-safe: compiled <see cref="Regex"/> instances are stateless after
/// construction, and the denylist is an immutable snapshot captured at startup.
/// </para>
/// </remarks>
public sealed class PatternSecretRedactor : ISecretRedactor
{
    private readonly IReadOnlyList<string> _denylistPatterns;
    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> _redactionPatterns;

    /// <summary>
    /// Initializes a new instance using the meta-harness configuration for the denylist.
    /// </summary>
    /// <param name="config">
    /// The meta-harness configuration monitor. Only <see cref="MetaHarnessConfig.SecretsRedactionPatterns"/>
    /// is read, and only at construction time. Changes to the config after startup are ignored.
    /// </param>
    public PatternSecretRedactor(IOptionsMonitor<MetaHarnessConfig> config)
        : this(config.CurrentValue.SecretsRedactionPatterns ?? [])
    {
    }

    /// <summary>
    /// Initializes a new instance with an explicit denylist. Intended for testing.
    /// </summary>
    /// <param name="denylistPatterns">
    /// Case-insensitive substrings matched against config key names by <see cref="IsSecretKey"/>.
    /// Must not be null; pass an empty list to disable key filtering.
    /// </param>
    public PatternSecretRedactor(IReadOnlyList<string> denylistPatterns)
    {
        _denylistPatterns = denylistPatterns;
        _redactionPatterns = Array.AsReadOnly(BuildRedactionPatterns());
    }

    /// <summary>
    /// Above this length the structural JSON pass in <see cref="Redact"/> is skipped in favor of the
    /// regex-only fallback. <see cref="Redact"/> is called on tool outputs specifically <em>above</em>
    /// a token-compression threshold (see <c>ToolOutputCompressionBehavior</c>), so nothing bounds
    /// how large an input reaches this method — parsing an unbounded payload into a full
    /// <see cref="JsonNode"/> tree on that path would trade a bounded regex scan for unbounded
    /// allocation. Payloads above this size keep only regex-based protection: a secret escaped inside
    /// nested JSON in a payload this large goes unredacted by the structural pass, but every
    /// unescaped/URL-embedded shape the regex patterns already cover is still caught. Mirrored as
    /// <c>ToolPayloadRedactor.MaxStructuralRedactionCeiling</c> (Application layer) so a caller with
    /// no size cap of its own — <c>ToolDiagnosticsMiddleware</c>'s persisted-record path — can
    /// withhold rather than preview a payload this large, instead of risking an unredacted secret in
    /// what it persists. Keep the two values in sync if either changes.
    /// </summary>
    private const int MaxStructuralRedactionLength = 64 * 1024;

    /// <summary>
    /// How many levels of "a string value that is itself JSON" <see cref="RedactNode"/> will parse
    /// and recurse into. Bounds work against adversarial input (a string containing a JSON-escaped
    /// string containing a JSON-escaped string, arbitrarily deep) — real tool payloads nest at most
    /// one level (a request body serialized as a string field), so 2 is generous headroom, not a
    /// tight fit.
    /// </summary>
    private const int MaxEmbeddedJsonDepth = 2;

    private static readonly JsonDocumentOptions StructuralParseOptions = new() { MaxDepth = 32 };

    /// <inheritdoc />
    /// <remarks>
    /// When <paramref name="input"/> looks like JSON (starts with <c>{</c> or <c>[</c>, after
    /// trimming) and is under <see cref="MaxStructuralRedactionLength"/>, redaction walks the parsed
    /// structure instead of scanning the raw text: a value whose parent key names a secret is
    /// replaced outright, and a string value that is itself JSON (the escaped-nested-payload shape,
    /// e.g. a request body serialized as a string field, up to <see cref="MaxEmbeddedJsonDepth"/>
    /// levels deep) is recursively parsed and redacted the same way. This closes the gap the
    /// regex-only pass cannot: a key/value pair whose surrounding quotes are escaped
    /// (<c>\"api_key\":\"secret\"</c>) never matches a pattern written for the unescaped shape. Every
    /// leaf string — JSON or not — still runs through the regex passes below, so free-text secrets
    /// embedded in an otherwise ordinary value (a token in a URL query string) are still caught.
    /// Falls back to the regex-only pass for non-JSON input, oversized input, or input that merely
    /// looks like JSON but fails to parse (unchanged from before this pass existed).
    /// </remarks>
    public string? Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        if (!LooksLikeJson(input) || input.Length > MaxStructuralRedactionLength)
            return RedactFreeText(input);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(input, documentOptions: StructuralParseOptions);
        }
        catch (JsonException)
        {
            return RedactFreeText(input);
        }

        bool changed;
        JsonNode? redacted;
        try
        {
            (changed, redacted) = RedactNode(node, depth: 0);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            // Two independent risks the walk can hit, both handled the same way — degrade to the
            // regex-only pass rather than let a redaction attempt throw uncaught:
            //  - ArgumentException: duplicate property names are legal JSON (RFC 8259) that
            //    JsonNode.Parse tolerates but JsonObject's dictionary backing rejects the moment it's
            //    materialized (RedactObject's ToArray() call) — a genuinely third-party-controlled
            //    shape (an MCP server or HTTP tool response), not something to fail the caller over.
            //  - RegexMatchTimeoutException: RedactStringLeaf's RedactFreeText call scans every JSON
            //    string leaf against every pattern in _redactionPatterns, each with its own
            //    MatchTimeout — a large tree has many leaves, so the walk's aggregate exposure to a
            //    single pathological leaf tripping a timeout is real, where the single flat scan this
            //    pass supplements only ever risked it once per Redact() call.
            // Regex-only still redacts what it can either way.
            return RedactFreeText(input);
        }

        // Only re-serialize when something was actually redacted — otherwise a JSON payload with no
        // secrets would be silently reformatted (whitespace normalized, non-ASCII escaped by the
        // default encoder) on every call, which is unnecessary churn and breaks the "return the
        // original reference when nothing matched" no-allocation guarantee this method documents.
        return changed ? redacted!.ToJsonString() : input;
    }

    /// <summary>
    /// Cheap pre-check before attempting a JSON parse: does the trimmed input start with an object,
    /// array, or string opener? The string case matters as much as the other two: a tool's result (or
    /// a request-body value serialized as a string field) that is itself a JSON-encoded string —
    /// <c>"{\"api_key\":\"sk-1\"}"</c>, exactly what <c>JsonSerializer.Serialize(someString)</c>
    /// produces — parses as a <see cref="JsonValue"/> whose text is the nested document; without this
    /// case that whole document is treated as plain text and only the regex-only fallback ever sees
    /// it. Exception-driven control flow (attempting <see cref="JsonNode"/>.Parse on every plain-text
    /// log line or config value just to catch the failure) costs far more than the regex passes this
    /// structural pass exists to supplement, so ordinary non-JSON input is rejected with a character
    /// comparison instead of a parse attempt.
    /// </summary>
    private static bool LooksLikeJson(string input)
    {
        var trimmed = input.AsSpan().Trim();
        return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[' || trimmed[0] == '"');
    }

    /// <summary>
    /// Applies the free-text regex pattern set to <paramref name="input"/>. This is the entire
    /// pre-JSON-aware behavior of <see cref="Redact"/>, extracted so it can serve both as the
    /// fallback for non-JSON/oversized input and as the leaf-level scan <see cref="RedactNode"/> runs
    /// over each JSON string value.
    /// </summary>
    private string RedactFreeText(string input)
    {
        var result = input;
        foreach (var (pattern, replacement) in _redactionPatterns)
            result = pattern.Replace(result, replacement);
        return result;
    }

    /// <summary>
    /// Recursively redacts a parsed JSON node — dispatches to the shape-specific helper for an
    /// object, array, or string leaf; a number/bool/null leaf is left untouched.
    /// </summary>
    /// <returns>
    /// Whether anything in the subtree changed, and the (possibly same, possibly replaced) node.
    /// </returns>
    private (bool Changed, JsonNode? Node) RedactNode(JsonNode? node, int depth) => node switch
    {
        JsonObject obj => RedactObject(obj, depth),
        JsonArray array => RedactArray(array, depth),
        JsonValue value when value.TryGetValue<string>(out var text) => RedactStringLeaf(value, text, depth),
        _ => (false, node),
    };

    /// <summary>
    /// A property whose key names a secret (<see cref="IsSecretKeyName"/>) has its value replaced
    /// with <c>"[REDACTED]"</c> — unless it is already exactly that placeholder, in which case it is
    /// left alone so an already-redacted document reports no change; every other property is
    /// recursed into via <see cref="RedactNode"/>.
    /// </summary>
    private (bool Changed, JsonNode? Node) RedactObject(JsonObject obj, int depth)
    {
        var changed = false;
        // ToArray() first: mutating a JsonObject's values in place while enumerating it throws,
        // since JsonObject is itself the enumerable being walked.
        foreach (var (key, value) in obj.ToArray())
        {
            if (IsSecretKeyName(key))
            {
                if (value is JsonValue existing && existing.TryGetValue<string>(out var existingText)
                    && existingText == "[REDACTED]")
                    continue;

                obj[key] = JsonValue.Create("[REDACTED]");
                changed = true;
            }
            else
            {
                var (childChanged, childNode) = RedactNode(value, depth);
                if (childChanged)
                {
                    changed = true;
                    // An object/array child that changed was mutated in place (RedactNode returns
                    // the SAME instance for those cases) and is therefore still correctly parented
                    // under obj — reassigning it to its own slot throws "the node already has a
                    // parent". Only a genuinely new node (the JsonValue leaf-replacement case) needs
                    // to be written back.
                    if (!ReferenceEquals(childNode, value))
                        obj[key] = childNode;
                }
            }
        }
        return (changed, obj);
    }

    /// <summary>Each element is recursed into positionally via <see cref="RedactNode"/>.</summary>
    private (bool Changed, JsonNode? Node) RedactArray(JsonArray array, int depth)
    {
        var changed = false;
        for (var i = 0; i < array.Count; i++)
        {
            var existingElement = array[i];
            var (childChanged, childNode) = RedactNode(existingElement, depth);
            if (childChanged)
            {
                changed = true;
                if (!ReferenceEquals(childNode, existingElement))
                    array[i] = childNode;
            }
        }
        return (changed, array);
    }

    /// <summary>
    /// A string leaf that itself looks like and parses as JSON is redacted recursively and
    /// re-serialized back into the string slot (the escaped-nested-payload case), up to
    /// <paramref name="depth"/> reaching <see cref="MaxEmbeddedJsonDepth"/>; any other string leaf is
    /// scanned via <see cref="RedactFreeText"/>.
    /// </summary>
    private (bool Changed, JsonNode? Node) RedactStringLeaf(JsonValue value, string text, int depth)
    {
        if (depth < MaxEmbeddedJsonDepth && LooksLikeJson(text))
        {
            JsonNode? nested;
            try
            {
                nested = JsonNode.Parse(text, documentOptions: StructuralParseOptions);
            }
            catch (JsonException)
            {
                nested = null;
            }

            if (nested is not null)
            {
                var (childChanged, childNode) = RedactNode(nested, depth + 1);
                return childChanged ? (true, JsonValue.Create(childNode!.ToJsonString())) : (false, value);
            }
        }

        var redactedText = RedactFreeText(text);
        return redactedText == text ? (false, value) : (true, JsonValue.Create(redactedText));
    }

    /// <inheritdoc />
    public bool IsSecretKey(string configKey)
    {
        if (string.IsNullOrEmpty(configKey))
            return false;

        foreach (var pattern in _denylistPatterns)
        {
            if (configKey.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Every compiled pattern below gets a match timeout — this redactor now sits on a client-facing
    // hot path (streamed tool-call arguments/results), fed by tool-controlled text, and the pattern
    // set is expected to keep growing. None of today's patterns has a super-linear backtracking path,
    // but a timeout is cheap insurance that a future addition can't turn a large tool result into a
    // CPU stall on the request thread.
    //
    // 100ms was too tight in practice: reproduced RegexMatchTimeoutException on a 20-CHARACTER input
    // ("AccountKey=[REDACTED]") under nothing more exotic than a full-solution parallel test run —
    // CPU scheduling contention alone, not backtracking, pushed a trivial match past the deadline.
    // RegexMatchTimeoutException propagates uncaught from RedactFreeText (the regex-only path has no
    // exception handling of its own — see Redact's fail-loud contract), so a timeout this tight risks
    // throwing out of every caller under ordinary production load, not just synthetic pathological
    // input. 2 seconds still bounds a genuinely catastrophic pattern (today's patterns would need
    // orders of magnitude worse backtracking to approach even the old 100ms) while sitting far clear
    // of realistic scheduling jitter.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    // Keys covered by both the JSON-quoted pattern and the generic key=value/key:value pattern below —
    // kept as one shared alternation so the two patterns can't drift out of sync (a key redacted in one
    // shape but not the other was exactly the bug that motivated this constant). Extended (security
    // review on #391/#392) after measuring that common real-world secret key names were missing:
    // x-api-key, refresh/id/bearer tokens, private/secret-access keys, authorization, credentials,
    // passphrases, and subscription-key headers (Ocp-Apim-Subscription-Key) all passed through
    // unredacted before this list included them.
    private const string SecretKeyAlternation =
        "api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|bearer[_-]?token|token|" +
        "secret[_-]?key|secret[_-]?access[_-]?key|client[_-]?secret|private[_-]?key|secret|" +
        "password|pwd|passphrase|credentials?|authorization|auth[_-]?token|" +
        "account[_-]?key|shared[_-]?access[_-]?key|connection[_-]?string|sas[_-]?token|" +
        "subscription[_-]?key|x[_-]api[_-]key|ocp[_-]apim[_-]subscription[_-]key";

    // Anchored (whole-key) match for the structural JSON walk in RedactNode — deliberately distinct
    // from IsSecretKey/_denylistPatterns, which is a separate, config-driven, substring-match
    // denylist used only for filtering config-snapshot keys. Conflating the two would change
    // IsSecretKey's contract for its existing callers. Built once from the same SecretKeyAlternation
    // the free-text patterns use, so a JSON object key and the equivalent free-text "key=value" shape
    // can never redact one and miss the other. The key is trimmed before matching — a key with
    // incidental leading/trailing whitespace (a sloppily-serialized tool argument) is still exactly
    // "api_key" as far as an attacker or a careless tool author is concerned.
    private static readonly Regex SecretKeyNameRegex = new(
        $"^(?:{SecretKeyAlternation})$", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout);

    private static bool IsSecretKeyName(string key) => SecretKeyNameRegex.IsMatch(key.Trim());

    private static (Regex Pattern, string Replacement)[] BuildRedactionPatterns() =>
    [
        // Bearer tokens: case-insensitive; replaces entire match preserving the "Bearer" prefix.
        // The character class [A-Za-z0-9\-._~+/] excludes '[' and ']', so "[REDACTED]" is immune.
        (
            new Regex(
                @"Bearer\s+[A-Za-z0-9\-._~+/]+=*",
                RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),
            "Bearer [REDACTED]"
        ),

        // Connection string value segments: AccountKey=..., Password=..., etc.
        // Negative lookahead (?!\[REDACTED\]) ensures idempotency — already-redacted values
        // are not re-matched on a second Redact() call.
        (
            new Regex(
                @"(?i)(AccountKey|Password|pwd|SharedAccessKey)\s*=\s*(?!\[REDACTED\])[^;""'\s]+",
                RegexOptions.Compiled, MatchTimeout),
            "$1=[REDACTED]"
        ),

        // Generic key=value / key:value secret pairs, covering both quoted and unquoted values (a
        // bare key with a quoted value — "api_key: \"value\"" in YAML, "api_key=\"value\"" in a log
        // line — is a normal, common shape the JSON-quoted pattern below does NOT cover, since that
        // pattern requires the KEY to be quoted too).
        //
        // The value alternation tries, in order: a double-quoted string, a single-quoted string, then
        // a bounded unquoted run. Two mistakes this specifically avoids, both shipped and caught by
        // review before merge:
        //   1. A bare \S+ is greedy across the whole rest of a whitespace-free string, so on compact
        //      JSON (no spaces) a key found inside a serialized string value (e.g. "...?api_key=abc123"
        //      embedded in a URL) would consume every remaining character in the document — the
        //      closing quote, every later key, the closing brace — corrupting the JSON and silently
        //      dropping the rest of the payload.
        //   2. Narrowing the value class to [^;"'\s]+ alone (excluding quotes) fixes (1) but makes the
        //      pattern quote-hostile: a value that starts with a quote has nothing left to match at
        //      that position, so the match fails outright and the secret passes through completely
        //      unredacted — worse than the greedy-corruption bug it replaced, since (1) still exposed
        //      the leaked leading string of the secret while (2) exposed the whole one. The quoted
        //      alternatives above must be tried first so a quoted value is matched by its own bounded
        //      class instead of falling through to the unquoted one.
        //
        // The separator is captured (not hardcoded) so the replacement preserves whichever of "=" or
        // ":" the input actually used, rather than silently rewriting "api_key: value" into
        // "api_key=value" and potentially invalidating the surrounding document.
        // Negative lookahead (?!\[REDACTED\]) ensures idempotency. A second negative lookahead,
        // (?!Bearer\s), defers to the Bearer-token pattern above for a value shaped "Bearer <token>" —
        // added when SecretKeyAlternation grew "authorization"/"auth_token": without it, this pattern's
        // unquoted-value branch (which stops at the first whitespace) matched just the word "Bearer" as
        // the "value" for a key like "Authorization", corrupting "Authorization: Bearer [REDACTED]"
        // (correctly redacted by the pattern above) into "Authorization: [REDACTED] [REDACTED]".
        (
            new Regex(
                $@"(?i)({SecretKeyAlternation})(\s*[=:]\s*)(?!\[REDACTED\])(?!Bearer\s)(?:""[^""]*""|'[^']*'|[^;""'\s]+)",
                RegexOptions.Compiled, MatchTimeout),
            "$1$2[REDACTED]"
        ),

        // JSON-quoted key/value secret pairs: "api_key":"value". The generic pattern above requires
        // an unquoted key, so it never matches this shape — and tool payloads streamed to clients are
        // routinely JSON (function-call arguments, tool results serialized as objects).
        // The value matcher (?:\\.|[^"\\])* is escape-aware — it treats "\"" as part of the value
        // rather than ending the match there, unlike a plain [^"]* which stops at the first quote
        // character regardless of the preceding backslash and truncates the match mid-value, leaking
        // the remainder of an escaped secret and corrupting the surrounding JSON in the output.
        // Negative lookahead (?!\[REDACTED\]) ensures idempotency.
        (
            new Regex(
                $@"(?i)""({SecretKeyAlternation})""\s*:\s*""(?!\[REDACTED\])(?:\\.|[^""\\])*""",
                RegexOptions.Compiled, MatchTimeout),
            @"""$1"":""[REDACTED]"""
        ),
    ];
}
