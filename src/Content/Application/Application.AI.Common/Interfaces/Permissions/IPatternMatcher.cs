namespace Application.AI.Common.Interfaces.Permissions;

/// <summary>
/// Matches tool names and operations against permission rule patterns.
/// Supports exact, prefix (e.g., "git:*"), and glob patterns.
/// </summary>
public interface IPatternMatcher
{
    /// <summary>
    /// Tests whether a value matches a pattern.
    /// </summary>
    /// <param name="pattern">The pattern to match against (exact, "prefix:*", or glob).</param>
    /// <param name="value">The value to test.</param>
    /// <returns>True if the value matches the pattern.</returns>
    bool IsMatch(string pattern, string value);
}
