namespace Domain.Common.Helpers;

/// <summary>
/// Filesystem path-containment checks shared across layers. Centralises the "is this path the same as,
/// or nested under, that directory?" predicate so skill/agent path-ownership decisions use one
/// authoritative implementation rather than diverging copies (a divergence here is security-relevant:
/// it governs which directories an agent's skills are discovered from and disclosed under).
/// </summary>
public static class PathScope
{
    /// <summary>
    /// Returns the absolute, separator-trimmed form of <paramref name="path"/> so two paths can be
    /// compared for containment without being tripped up by relative segments or trailing separators.
    /// </summary>
    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="target"/> equals, or is nested under,
    /// <paramref name="baseDir"/>. Both paths are normalised first. Comparison is case-insensitive on
    /// Windows and ordinal elsewhere. The trailing-separator check on the prefix ensures a sibling whose
    /// name merely starts with the base (e.g. <c>C:\skills-config</c> against <c>C:\skills</c>) is not
    /// mistaken for a child.
    /// </summary>
    public static bool IsSameOrUnder(string target, string baseDir) =>
        IsSameOrUnderNormalized(Normalize(target), Normalize(baseDir));

    /// <summary>
    /// Containment check for paths that are already <see cref="Normalize"/>d. Prefer this overload when
    /// the base directory is compared against many targets, so the base is normalised only once.
    /// </summary>
    public static bool IsSameOrUnderNormalized(string normalizedTarget, string normalizedBase)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(normalizedTarget, normalizedBase, comparison)
            || normalizedTarget.StartsWith(normalizedBase + Path.DirectorySeparatorChar, comparison);
    }
}
