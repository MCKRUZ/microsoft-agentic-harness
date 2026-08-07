namespace Tests.Common;

/// <summary>
/// Locates the repository root for tests that need to read a file checked in alongside the code —
/// an OpenTelemetry collector config, a dashboard definition, a sample SKILL.md.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is anchored on the solution file and not on <c>.git</c>.</strong> Five test
/// helpers grew their own copy of this walk, and four of them looked for a <c>.git</c>
/// <em>directory</em>. In a git worktree <c>.git</c> is a <em>file</em> containing a
/// <c>gitdir:</c> pointer, so the walk ran past the root, off the top of the drive, and threw.
/// Because these helpers are called from static initializers, the throw took whole test classes
/// with it — 19 tests across four classes (#293).
/// </para>
/// <para>
/// That matters more than a normal test-only bug because a worktree is the arrangement this repo's
/// own guidance recommends: the gate runner reads the working checkout, so running gates while
/// doing other work requires a second copy. The recommended setup could not run these tests.
/// </para>
/// <para>
/// <c>src/AgenticHarness.slnx</c> is the right anchor because it is unambiguous in both
/// arrangements: it is a file in a worktree and a file in the main checkout, so there is no
/// file-versus-directory question to get wrong. Accepting "<c>.git</c> as either a file or a
/// directory" would have fixed the reported symptom while leaving the same trap for the next
/// copy of the walk — which is why the four copies are gone rather than patched.
/// </para>
/// <para>
/// This does mean the root is defined as "the directory containing the solution", not "the
/// directory git considers the root". For reading checked-in files those are the same directory,
/// and for a worktree the former is the one a test actually wants.
/// </para>
/// </remarks>
public static class RepoRoot
{
    /// <summary>
    /// The file whose presence marks the repository root, as path segments. The error message is
    /// derived from these rather than restating them, so renaming the solution cannot leave the
    /// check looking for one file while the diagnostic names another.
    /// </summary>
    private static readonly string[] AnchorSegments = ["src", "AgenticHarness.slnx"];

    private static readonly string AnchorDisplay = string.Join('/', AnchorSegments);

    /// <remarks>
    /// Anchored on <see cref="AppContext.BaseDirectory"/>, not the current working directory. Three
    /// of the implementations this replaced used the latter, which any test that calls
    /// <c>Directory.SetCurrentDirectory</c> can move out from under an unrelated test. The assembly
    /// location cannot be moved that way.
    /// </remarks>
    private static readonly Lazy<string> Cached = new(() => Find(AppContext.BaseDirectory));

    /// <summary>
    /// The repository root, resolved once per process.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The anchor file was not found in any ancestor directory.
    /// </exception>
    public static string Path => Cached.Value;

    /// <summary>
    /// Combines <see cref="Path"/> with the given repo-relative segments.
    /// </summary>
    /// <param name="segments">Path segments below the repository root.</param>
    /// <returns>An absolute path.</returns>
    public static string Combine(params string[] segments) =>
        System.IO.Path.Combine([Path, .. segments]);

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for the anchor file.
    /// </summary>
    /// <param name="startDirectory">The directory to start from.</param>
    /// <returns>The directory containing <c>src/AgenticHarness.slnx</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// No ancestor contains the anchor. The message names the anchor and the starting point,
    /// because the failure surfaces from a static initializer where the stack trace alone says
    /// very little about what was actually being looked for.
    /// </exception>
    public static string Find(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var dir = startDirectory;
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine([dir, .. AnchorSegments])))
                return dir;

            dir = System.IO.Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            $"Could not find the repository root (a directory containing {AnchorDisplay}) " +
            $"starting from '{startDirectory}'.");
    }
}
