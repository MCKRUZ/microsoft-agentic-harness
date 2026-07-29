using Domain.Common.Helpers;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Resolves and validates the on-disk location of the governance-state database.
/// </summary>
/// <remarks>
/// The configured path is attacker-relevant: it names the file holding approval verdicts.
/// A path that escapes the application directory (via <c>..</c> segments, or an absolute
/// path pointing at a network share) would place that file somewhere the host does not
/// control. Resolution therefore normalizes first and then asserts containment, rather than
/// trusting the configured string.
/// </remarks>
public static class GovernanceStatePaths
{
    /// <summary>
    /// Normalizes <paramref name="configuredPath"/> against the application base directory and
    /// verifies the result stays under it.
    /// </summary>
    /// <param name="configuredPath">The configured database path, absolute or relative.</param>
    /// <param name="baseDirectory">
    /// The permitted root. Defaults to <see cref="AppContext.BaseDirectory"/>; overridable so
    /// tests can assert the containment rule without writing to the test host's directory.
    /// </param>
    /// <returns>The absolute, validated database file path.</returns>
    /// <exception cref="ArgumentException">
    /// The path is blank, resolves outside <paramref name="baseDirectory"/>, or names a
    /// location with no parent directory.
    /// </exception>
    public static string Resolve(string configuredPath, string? baseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        var root = PathScope.Normalize(baseDirectory ?? AppContext.BaseDirectory);
        var fullPath = PathScope.Normalize(Path.Combine(root, configuredPath));

        if (!PathScope.IsSameOrUnderNormalized(fullPath, root))
        {
            throw new ArgumentException(
                "AppConfig:AI:Governance:DurableState:DatabasePath resolves outside the application " +
                "directory. Configure a path relative to the application base directory.",
                nameof(configuredPath));
        }

        // Containment alone would admit the root directory itself, which names a directory rather than
        // a database file. PathScope.Comparison keeps this check on the same case-sensitivity rules as
        // the containment check above, so the two cannot drift apart.
        if (string.Equals(fullPath, root, PathScope.Comparison))
        {
            throw new ArgumentException(
                "AppConfig:AI:Governance:DurableState:DatabasePath must name a file inside the " +
                "application directory, not the directory itself.",
                nameof(configuredPath));
        }

        if (string.IsNullOrEmpty(Path.GetDirectoryName(fullPath)))
        {
            throw new ArgumentException(
                "AppConfig:AI:Governance:DurableState:DatabasePath has no containing directory.",
                nameof(configuredPath));
        }

        return fullPath;
    }

    /// <summary>
    /// Creates the containing directory for a resolved database path. Called lazily, on first
    /// context materialization, so a host that never enables durable state creates nothing.
    /// </summary>
    /// <param name="resolvedDatabasePath">A path already returned by <see cref="Resolve"/>.</param>
    public static void EnsureDirectory(string resolvedDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedDatabasePath);

        var directory = Path.GetDirectoryName(resolvedDatabasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException(
                "Resolved governance-state database path has no containing directory.",
                nameof(resolvedDatabasePath));
        }

        Directory.CreateDirectory(directory);
    }
}
