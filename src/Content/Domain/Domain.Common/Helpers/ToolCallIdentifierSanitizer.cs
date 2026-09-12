namespace Domain.Common.Helpers;

/// <summary>
/// Narrows a tool call id or tool name to the identifier shape (<see cref="IdentifierSanitizer"/>),
/// truncated and collision-guarded, before either is used anywhere a hostile provider- or
/// model-supplied value must not reach unbounded and unrestricted (#513, #556).
/// </summary>
/// <remarks>
/// Extracted from <c>ToolCallTranscriptExtractor.SanitizeIdentifier</c> (#513, the replay-persistence
/// path) so the live AG-UI streaming path (#556) can apply the IDENTICAL deterministic transform to
/// the same raw value, rather than re-deriving its own — this codebase's own <see cref="IdentifierSanitizer"/>
/// doc comment names the cost of not doing this the first time (two independent, later-reconciled
/// implementations of the same hash-prefix step). Pure and side-effect-free by design: callers that
/// need to log a truncation/rewrite own that themselves, since what's safe to log (a correlation id,
/// never the raw value — CWE-117) differs per caller.
/// </remarks>
public static class ToolCallIdentifierSanitizer
{
    /// <summary>
    /// The longest a tool name or call id may be after sanitizing. Well above every provider's own
    /// limit, so a legitimate value is never truncated — a value that reaches this ceiling is already
    /// suspicious on length alone, independent of what characters it contains.
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Hex characters of the collision-guard hash suffix. Deliberately longer than
    /// <c>BundleOwnedMcpToolNaming</c>'s 10-char precedent (40 bits): this path sanitizes
    /// attacker-influenceable CallIds/names (#556 security review), where a birthday-bound collision
    /// against a chosen target string is realistic offline work at 40 bits but not at 64.
    /// </summary>
    private const int HashSuffixHexLength = 16;

    private const string HashSuffixSeparator = "_";

    /// <param name="Value">
    /// The original raw value unchanged when <see cref="Changed"/> is <see langword="false"/>;
    /// otherwise the sanitized, truncated, collision-guarded replacement. Either way, the value is
    /// already identifier-shaped by the time it is returned (#633) — wrapped as a
    /// <see cref="SanitizedIdentifier"/> so a caller cannot pass it, or a raw value in its place, to a
    /// consumer that requires one already sanitized without a compile error.
    /// </param>
    /// <param name="Changed">
    /// Whether the raw value needed truncation, character-class narrowing, or both, or was already
    /// shaped like this method's own rewritten output — the signal a caller uses to decide whether to
    /// log a warning. A caller logging on this can occasionally warn about a legitimate raw value that
    /// merely resembles rewritten output (see <see cref="IsInOutputShape"/>); that's the accepted cost
    /// of keeping the two output namespaces disjoint.
    /// </param>
    public readonly record struct Result(SanitizedIdentifier Value, bool Changed);

    /// <summary>
    /// Sanitizes <paramref name="raw"/>. Mapping every disallowed character to the same <c>'_'</c>
    /// collapses information — two distinct raw values (e.g. <c>"call#1"</c> and <c>"call$1"</c>) can
    /// sanitize to an identical string — so a hash suffix of the original raw value is appended
    /// whenever sanitization actually changed something, keeping distinct raw values distinct after
    /// sanitizing (matching <c>BundleOwnedMcpToolNaming.SanitizeWithCollisionGuard</c>'s own precedent
    /// for the identical reason).
    /// </summary>
    /// <remarks>
    /// The passthrough branch (nothing to rewrite) and the rewrite branch must produce disjoint output
    /// sets, or the same collapse this guard exists to prevent reappears one level up: a rewritten
    /// value is itself already clean and short, so it is a fixed point of the passthrough branch — a
    /// second, distinct raw value that happens to equal some other raw value's rewritten output would
    /// pass through unchanged and land on that same output (#556 security review). <see cref="IsInOutputShape"/>
    /// closes this by forcing any raw value already shaped like rewritten output through the rewrite
    /// branch too, so every passthrough output is guaranteed NOT to look like a rewritten one.
    /// </remarks>
    public static Result Sanitize(string raw)
    {
        var truncated = raw.Length > MaxLength ? raw[..MaxLength] : raw;
        var sanitized = IdentifierSanitizer.Sanitize(truncated);
        var changed = truncated.Length != raw.Length || sanitized != truncated || IsInOutputShape(sanitized);

        // IdentifierSanitizer.Sanitize itself already takes the zero-allocation path for an
        // already-clean value — this only adds the truncation/output-shape checks on top.
        if (!changed)
            return new Result(new SanitizedIdentifier(raw), Changed: false);

        var suffix = $"{HashSuffixSeparator}{Sha256HexPrefixHelper.Compute(raw, HashSuffixHexLength)}";
        var keep = Math.Max(0, MaxLength - suffix.Length);
        var basePart = sanitized.Length > keep ? sanitized[..keep] : sanitized;
        return new Result(new SanitizedIdentifier($"{basePart}{suffix}"), Changed: true);
    }

    /// <summary>
    /// True when <paramref name="value"/> already ends in this method's own rewrite shape — a
    /// separator followed by exactly <see cref="HashSuffixHexLength"/> lowercase hex characters. Used
    /// only to force such a value through the rewrite branch (see <see cref="Sanitize"/>'s remarks);
    /// a false positive here just means an occasional legitimate value gets an extra suffix, never a
    /// missed rewrite.
    /// </summary>
    private static bool IsInOutputShape(string value)
    {
        if (value.Length <= HashSuffixHexLength || value[^(HashSuffixHexLength + 1)] != HashSuffixSeparator[0])
            return false;

        var suffix = value.AsSpan(value.Length - HashSuffixHexLength);
        foreach (var c in suffix)
        {
            if (!Uri.IsHexDigit(c) || char.IsUpper(c))
                return false;
        }

        return true;
    }
}
