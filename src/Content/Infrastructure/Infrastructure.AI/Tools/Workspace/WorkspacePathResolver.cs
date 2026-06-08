using Domain.AI.Workspace;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// Shared path-resolution helpers for the workspace tools. Centralises the
/// canonicalisation + sandbox-escape guard so each tool implements its own
/// logic the same way and stays consistent.
/// </summary>
/// <remarks>
/// <para>
/// The harness uses a closed-by-default capability model: the agent supplies a
/// path relative to the working copy; the resolver combines it with the
/// sandbox-injected <see cref="WorkspaceContext.WorkingCopyPath"/>, calls
/// <see cref="Path.GetFullPath(string)"/> to normalise, and then verifies the
/// resolved path still lives under the working copy. Anything else throws
/// <see cref="UnauthorizedAccessException"/> — caller catches and surfaces a
/// generic refusal so we never leak host filesystem structure into LLM context.
/// </para>
/// </remarks>
public static class WorkspacePathResolver
{
    /// <summary>
    /// Resolves <paramref name="relativePath"/> against
    /// <paramref name="workspace"/>'s working copy and verifies it does not
    /// escape. Returns the canonical absolute path on success.
    /// </summary>
    /// <param name="workspace">The active workspace context.</param>
    /// <param name="relativePath">Path supplied by the LLM. May be absolute (rejected unless inside the workspace) or relative.</param>
    /// <returns>The canonical absolute path inside the working copy.</returns>
    /// <exception cref="ArgumentException">When <paramref name="relativePath"/> is null, empty, or whitespace.</exception>
    /// <exception cref="UnauthorizedAccessException">When the resolved path lies outside the working copy.</exception>
    public static string Resolve(WorkspaceContext workspace, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var workingCopy = Path.GetFullPath(workspace.WorkingCopyPath);

        // Path.IsPathRooted handles both absolute paths and Windows drive-rooted forms.
        // When the agent supplies a rooted path we still canonicalise + verify it lives
        // under the working copy. When it supplies a relative path we join first.
        var combined = Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.GetFullPath(Path.Combine(workingCopy, relativePath));

        var workingCopyWithSep = workingCopy.EndsWith(Path.DirectorySeparatorChar)
            ? workingCopy
            : workingCopy + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(workingCopyWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, workingCopy, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "Path is outside the sandbox-injected working copy.");
        }

        return combined;
    }

    /// <summary>
    /// Converts <paramref name="absolutePath"/> back to a working-copy-relative
    /// path using forward slashes so the resulting string is usable as a
    /// <c>GitRepoTarget.WorkingPath</c> and a <c>ChangeEdit.Target</c>.
    /// </summary>
    /// <param name="workspace">The active workspace context.</param>
    /// <param name="absolutePath">Absolute path inside the working copy. Must have been resolved via <see cref="Resolve"/>.</param>
    /// <returns>The relative path with <c>/</c> separators, suitable for cross-platform use in change proposals.</returns>
    public static string ToRelative(WorkspaceContext workspace, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        var workingCopy = Path.GetFullPath(workspace.WorkingCopyPath);
        var rel = Path.GetRelativePath(workingCopy, absolutePath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
