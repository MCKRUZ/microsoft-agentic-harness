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

    /// <inheritdoc />
    public string? Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = input;
        foreach (var (pattern, replacement) in _redactionPatterns)
            result = pattern.Replace(result, replacement);

        return result;
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
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // Keys covered by both the JSON-quoted pattern and the generic key=value/key:value pattern below —
    // kept as one shared alternation so the two patterns can't drift out of sync (a key redacted in one
    // shape but not the other was exactly the bug that motivated this constant).
    private const string SecretKeyAlternation =
        "api[_-]?key|access[_-]?token|secret[_-]?key|client[_-]?secret|password|pwd|" +
        "account[_-]?key|shared[_-]?access[_-]?key|connection[_-]?string|sas[_-]?token";

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
        // Negative lookahead (?!\[REDACTED\]) ensures idempotency.
        (
            new Regex(
                $@"(?i)({SecretKeyAlternation})(\s*[=:]\s*)(?!\[REDACTED\])(?:""[^""]*""|'[^']*'|[^;""'\s]+)",
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
