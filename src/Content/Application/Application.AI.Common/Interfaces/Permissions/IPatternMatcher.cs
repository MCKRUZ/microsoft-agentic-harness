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

    /// <summary>
    /// Ranks how narrowly <paramref name="pattern"/> selects values, so a caller can prefer the most
    /// specific of several patterns that all match the same value. Higher is more specific.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the matcher knows its own pattern language, so the rank lives here rather than being
    /// re-derived by every consumer. Implementations must satisfy one contract: for any value, an
    /// exact-match pattern ranks strictly above every wildcard pattern that also matches it, and a
    /// match-everything pattern ranks lowest. Callers rely on that ordering to stop a catch-all rule
    /// from overriding a rule written for one specific name.
    /// </para>
    /// <para>
    /// Ranks are comparable only against each other; the absolute values carry no meaning.
    /// </para>
    /// </remarks>
    /// <param name="pattern">The pattern to rank.</param>
    /// <returns>A non-negative rank; higher values select more narrowly.</returns>
    int Specificity(string pattern);
}
