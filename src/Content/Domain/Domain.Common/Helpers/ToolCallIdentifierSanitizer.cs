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

    /// <summary>Hex characters of the collision-guard hash suffix — mirrors <c>BundleOwnedMcpToolNaming</c>'s own 5-byte suffix.</summary>
    private const int HashSuffixHexLength = 10;

    private const string HashSuffixSeparator = "_";

    /// <param name="Value">
    /// The original raw value unchanged when <see cref="Changed"/> is <see langword="false"/>;
    /// otherwise the sanitized, truncated, collision-guarded replacement.
    /// </param>
    /// <param name="Changed">
    /// Whether the raw value needed truncation, character-class narrowing, or both — the
    /// signal a caller uses to decide whether to log a warning.
    /// </param>
    public readonly record struct Result(string Value, bool Changed);

    /// <summary>
    /// Sanitizes <paramref name="raw"/>. Mapping every disallowed character to the same <c>'_'</c>
    /// collapses information — two distinct raw values (e.g. <c>"call#1"</c> and <c>"call$1"</c>) can
    /// sanitize to an identical string — so a hash suffix of the original raw value is appended
    /// whenever sanitization actually changed something, keeping distinct raw values distinct after
    /// sanitizing (matching <c>BundleOwnedMcpToolNaming.SanitizeWithCollisionGuard</c>'s own precedent
    /// for the identical reason).
    /// </summary>
    public static Result Sanitize(string raw)
    {
        var truncated = raw.Length > MaxLength ? raw[..MaxLength] : raw;
        var sanitized = IdentifierSanitizer.Sanitize(truncated);
        var changed = truncated.Length != raw.Length || sanitized != truncated;

        // IdentifierSanitizer.Sanitize itself already takes the zero-allocation path for an
        // already-clean value — this only adds the truncation check on top.
        if (!changed)
            return new Result(raw, Changed: false);

        var suffix = $"{HashSuffixSeparator}{Sha256HexPrefixHelper.Compute(raw, HashSuffixHexLength)}";
        var keep = Math.Max(0, MaxLength - suffix.Length);
        var basePart = sanitized.Length > keep ? sanitized[..keep] : sanitized;
        return new Result($"{basePart}{suffix}", Changed: true);
    }
}
