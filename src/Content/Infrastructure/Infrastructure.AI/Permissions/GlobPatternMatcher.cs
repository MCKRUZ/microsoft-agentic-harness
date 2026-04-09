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
}
