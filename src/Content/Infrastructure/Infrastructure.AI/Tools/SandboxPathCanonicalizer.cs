using Domain.Common.Helpers;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Resolves a filesystem path to its canonical absolute form, so an alias cannot sidestep a
/// comparison against the real location.
/// </summary>
/// <remarks>
/// Shared by <see cref="FileSystemService"/>'s per-path sandbox decisions and by
/// <see cref="FileSystemSandboxStartupValidator"/>'s boot-time overlap assertion. Both answer the
/// same question — "which directory does this path actually name?" — and a divergence between them
/// would let the startup assertion pass on a configuration the runtime check then treats as
/// overlapping (or the reverse), so the implementation lives in exactly one place.
/// </remarks>
internal static class SandboxPathCanonicalizer
{
    /// <summary>
    /// Returns the canonical absolute form of <paramref name="normalizedPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two aliasing mechanisms are covered, by two different steps.
    /// <see cref="PathScope.Normalize(string)"/> (which the caller has already applied — see the
    /// parameter contract) is what handles Windows 8.3 short names: on Windows its normalization
    /// expands any component that exists on disk, turning <c>PROGRA~1</c> into
    /// <c>Program Files</c>. The <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> step here
    /// then covers symlinks and junctions, which normalization leaves intact.
    /// </para>
    /// <para>
    /// A component that does not exist is left exactly as written, because there is nothing on
    /// disk to expand it against. That is harmless for both callers: a protected directory that
    /// does not exist holds nothing to protect, and a literal <c>AGENT-~1</c> path then names a
    /// genuinely different directory rather than aliasing the protected one.
    /// </para>
    /// <para>
    /// Hard links are deliberately <em>not</em> covered, and cannot be: a hard link is a second
    /// directory entry for the same file, not a reparse point, so there is no link target to
    /// resolve and the canonical form of a hard link is the hard link itself. No path-based
    /// canonicalization can close that, and no allowlist geometry can either — a hard link needs
    /// only the same volume as its target. It is closed instead by asking the operating system for
    /// the file's link count, via <see cref="HardLinkInspector"/>, at the point of use.
    /// </para>
    /// </remarks>
    /// <param name="normalizedPath">
    /// The path to canonicalize. Must already be <see cref="PathScope.Normalize"/>d. The
    /// precondition is load-bearing on the error path: when the entry cannot be inspected this
    /// method returns its input unchanged, so an un-normalized input would leak a relative or
    /// trailing-separator form into a containment comparison that assumes normalized operands.
    /// </param>
    /// <returns>The canonical absolute path, or <paramref name="normalizedPath"/> when the entry
    /// cannot be inspected (missing, unreadable, or not a supported path shape).</returns>
    public static string Canonicalize(string normalizedPath)
    {
        try
        {
            // One stat answers both "does it exist" and "is it a directory"; a missing entry throws
            // and is caught below. ResolveLinkTarget(returnFinalTarget) then canonicalizes the entry —
            // for a path that does not exist yet the normalized form is the best available answer.
            var info = File.GetAttributes(normalizedPath).HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(normalizedPath)
                : (FileSystemInfo)new FileInfo(normalizedPath);

            return PathScope.Normalize(info.ResolveLinkTarget(true)?.FullName ?? info.FullName);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException)
        {
            // Fail closed by returning the literal path: callers treat the result as the entry's
            // identity, and an unresolvable entry must not be credited with a different one.
            // NotSupportedException covers path shapes the runtime rejects outright (a Windows
            // path with an embedded colon, for example); without it the exception would escape
            // SearchFilesAsync unhandled instead of producing a clean deny.
            return normalizedPath;
        }
    }
}
