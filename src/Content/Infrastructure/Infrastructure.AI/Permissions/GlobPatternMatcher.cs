using Application.AI.Common.Interfaces.Permissions;

namespace Infrastructure.AI.Permissions;

/// <summary>
/// Pattern matcher supporting exact, prefix-wildcard, and full-wildcard patterns
/// for tool name and operation matching in the permission system.
/// </summary>
/// <remarks>
/// Supported patterns:
/// <list type="bullet">
///   <item><description>Exact: <c>"file_system"</c> matches only <c>"file_system"</c></description></item>
///   <item><description>Trailing wildcard: <c>"file_*"</c> matches <c>"file_system"</c>; <c>"git:*"</c> matches <c>"git:push"</c></description></item>
///   <item><description>Full wildcard: <c>"*"</c> matches anything</description></item>
/// </list>
/// Pattern matching is case-insensitive by default.
/// </remarks>
public sealed class GlobPatternMatcher : IPatternMatcher
{
    /// <inheritdoc />
    public bool IsMatch(string pattern, string value)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        if (string.IsNullOrEmpty(value))
            return false;

        if (pattern == "*")
            return true;

        // Trailing wildcard: "file_*" matches "file_system", "git:*" matches "git:push"
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ranks by how much of a candidate value the pattern pins down: the match-everything pattern
    /// constrains nothing and ranks 0, a trailing wildcard ranks by the length of the literal prefix it
    /// requires, and an exact pattern ranks one above its own length. That last <c>+1</c> is what
    /// guarantees the interface's contract — for any value <c>v</c>, a wildcard matching <c>v</c> can
    /// require at most <c>v.Length</c> literal characters, so the exact pattern's <c>v.Length + 1</c>
    /// always wins.
    /// </remarks>
    public int Specificity(string pattern)
    {
        // An empty pattern matches nothing (see IsMatch), so it can never be the selected rule; rank it
        // with the catch-all rather than inventing a separate case.
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
            return 0;

        return pattern.EndsWith('*') ? pattern.Length - 1 : pattern.Length + 1;
    }
}
