namespace Domain.Common.Helpers;

/// <summary>
/// Narrows a string to the character set most provider- or internally-generated identifiers are
/// expected to have: ASCII letters, digits, underscore, and hyphen. Centralizes the answer to
/// "what characters can an identifier actually contain" so every caller with that same narrow
/// question shares one implementation instead of re-deriving it — this codebase already paid for
/// that mistake once (see <see cref="Sha256HexPrefixHelper"/>'s own remarks on why its hash-prefix
/// step was extracted after two independent, identical implementations existed). Callers layer
/// their own collision-guard and length-budget policy on top of this primitive when they need
/// one; those differ per caller, so only the character-class scan itself is shared here.
/// </summary>
public static class IdentifierSanitizer
{
    /// <summary>
    /// Replaces every character in <paramref name="raw"/> outside <c>[A-Za-z0-9_-]</c> with
    /// <c>'_'</c>. Returns <paramref name="raw"/> itself, unallocated, when it is already clean —
    /// the common case for a well-formed identifier, and the reason this scans before it ever
    /// allocates rather than building the replacement eagerly and discarding it.
    /// </summary>
    public static string Sanitize(string raw)
    {
        var needsChange = false;
        foreach (var c in raw)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                continue;
            needsChange = true;
            break;
        }

        if (!needsChange)
            return raw;

        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            chars[i] = char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_';
        }

        return new string(chars);
    }
}
