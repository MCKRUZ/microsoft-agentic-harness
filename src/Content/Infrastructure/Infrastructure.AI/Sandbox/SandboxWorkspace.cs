using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// The two workspace-lifecycle operations both <see cref="ProcessSandboxLaunchPreparer"/> and
/// <see cref="DockerContainerLaunchPreparer"/> need, extracted here rather than left duplicated —
/// the whole point of splitting the two preparers apart (#371) is that a fix to shared mechanics
/// applies to both backends automatically, and a workspace directory is shared mechanics: neither
/// operation depends on which backend is creating or cleaning up the directory.
/// </summary>
internal static class SandboxWorkspace
{
    /// <summary>Restricts a freshly-created workspace directory to the current user, where the platform supports it.</summary>
    internal static void SetRestrictivePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Owner full access, other-EXECUTE (traverse) but not other-READ (list) — for a shared parent
    /// directory that individually-permissioned children live under. Lets a caller who already knows
    /// a specific child's name walk into it (required for the container's fixed unprivileged UID to
    /// resolve the bind-mount path down to a seeded, other-readable workspace), without letting an
    /// unrelated local account enumerate which workspace names currently exist.
    /// </summary>
    internal static void SetTraverseOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
    }

    /// <summary>
    /// Owner-only PLUS other-readable-and-traversable — widens a Docker-tier workspace beyond
    /// <see cref="SetRestrictivePermissions"/>'s owner-only 0700, ONLY for a session that seeds the
    /// workspace with content the container's own UID must be able to read (see
    /// <see cref="DockerSandboxSessionFactory.StartSessionAsync"/>'s seeded branch — the only call
    /// site). Every other Docker-tier workspace stays at 0700.
    /// </summary>
    /// <remarks>
    /// A container's process runs as a fixed, unprivileged UID (65534 — see
    /// <see cref="DockerContainerLaunchPreparer.BuildContainerParams"/>'s <c>User</c> field) that is
    /// never the harness host process's own UID, and Docker does not remap UIDs across a bind mount
    /// by default. Bare 0700 (owner-only) therefore denies the container ANY access to its own
    /// bind-mounted workspace — directory traversal fails before the container process can even stat
    /// a file inside it. This is a pre-existing gap in every Docker-tier workspace, not specific to a
    /// seeded one, but this method must NOT be called unconditionally for every Docker-tier session to
    /// close it: doing so would widen host directory permissions — readable by ANY local OS account,
    /// not just the container's own fixed UID — for every ordinary (non-bundle) Docker-tier workspace
    /// too, a strictly larger exposure than the seeded case this method exists to fix. (An earlier
    /// version of this change did exactly that; caught by /code-review.) The Process tier is
    /// unaffected and must keep <see cref="SetRestrictivePermissions"/> unchanged: that tier's process
    /// runs as the SAME host user, so 0700 is both sufficient and correct there — the workspace
    /// boundary there is the only isolation that tier has.
    /// <para>
    /// Not group-writable and not other-writable: this only grants what a read-only (or, once a
    /// bundle-owned session is ever granted <c>ToolCapability.FileWrite</c>, read-write-by-the-
    /// container-UID-via-the-bind-mount, not via host filesystem permissions) session needs.
    /// </para>
    /// </remarks>
    internal static void SetContainerAccessiblePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>Best-effort recursive delete of a workspace directory. Never throws.</summary>
    internal static void Cleanup(string path, ILogger logger, string backendName)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up {Backend} sandbox workspace {Path}", backendName, path);
        }
    }

    /// <summary>
    /// Recursively copies the contents of <paramref name="source"/> into an already-created
    /// <paramref name="destination"/> workspace directory — used to seed a sandbox session with
    /// caller-provided content (e.g. a bundle's staged files) that the fresh, empty workspace
    /// <see cref="DockerContainerLaunchPreparer.CreateWorkspace"/> creates has no visibility of on
    /// its own. A copy, not a link or bind of <paramref name="source"/> itself, deliberately: the
    /// caller may delete <paramref name="source"/> on a lifecycle independent of this session (see
    /// <see cref="Domain.AI.Sandbox.SandboxSessionRequest.WorkspaceSeedDirectory"/>'s remarks), so
    /// nothing about the running session may depend on it continuing to exist.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not an absolute path.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="source"/> does not exist.</exception>
    internal static void SeedFrom(string source, string destination)
    {
        if (!Path.IsPathRooted(source))
            throw new ArgumentException($"Seed source '{source}' must be an absolute path.", nameof(source));

        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Seed source directory '{source}' does not exist.");

        CopyDirectory(source, destination, depth: 0);
    }

    /// <summary>
    /// Deepest directory nesting <see cref="CopyDirectory"/> will descend before refusing to continue.
    /// Extraction's own <c>MaxEntryCount</c> guard (default 2000) permits a path nested up to ~2000
    /// levels deep within a single-path-length budget, and this is the only RECURSIVE walk anywhere in
    /// the staging/seeding pipeline over attacker-shaped content — extraction itself is a flat loop, and
    /// symlink validation uses the iterative <c>EnumerateFileSystemEntries(AllDirectories)</c>. Recursing
    /// thousands of frames risks <see cref="StackOverflowException"/>, which .NET cannot catch and which
    /// kills the entire host process — a bounded, catchable <see cref="InvalidOperationException"/> well
    /// before that point is the only sane failure mode for a directory tree no legitimate bundle needs.
    /// </summary>
    private const int MaxSeedDepth = 64;

    /// <summary>
    /// Copies files and subdirectories one level at a time, skipping any entry that is itself a
    /// symlink/junction (<see cref="FileSystemInfo.Attributes"/> carries <see cref="FileAttributes.ReparsePoint"/>)
    /// rather than following it — a symlink inside an untrusted source directory could otherwise
    /// point outside the source tree entirely (e.g. to a host path the caller has no business
    /// exposing to the sandbox), and this copy has no way to distinguish an intended relative link
    /// from that.
    /// </summary>
    /// <remarks>
    /// Every copied entry gets its permissions set explicitly (other-readable, dirs also
    /// other-executable/traversable) rather than left at whatever the host process's umask
    /// happens to produce — the destination is a Docker-tier workspace a container reads as a
    /// fixed, unprivileged UID that never matches the copying process's own UID (see
    /// <see cref="SetContainerAccessiblePermissions"/>), so relying on an ambient umask default
    /// (which may be as restrictive as 0077 on a hardened host) would make the seed silently
    /// unreadable on exactly the hosts most likely to run this feature.
    /// <para>
    /// Uses a single <see cref="DirectoryInfo.EnumerateFileSystemInfos()"/> scan per level rather
    /// than separate <c>EnumerateDirectories</c>/<c>EnumerateFiles</c> passes plus a fresh
    /// <c>DirectoryInfo</c>/<c>FileInfo</c> per entry just to read <c>LinkTarget</c> — that shape
    /// walked the same native directory listing twice and issued an extra stat/readlink syscall for
    /// every entry, symlink or not. <see cref="FileSystemInfo.Attributes"/> is already populated from
    /// the same scan that produced the entry, so both the dir-vs-file test and the symlink test are
    /// free here. Cost scales with bundle content size (nested plugin directories, vendored
    /// dependencies), so this isn't negligible for larger bundles.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The source tree nests deeper than <see cref="MaxSeedDepth"/>.</exception>
    private static void CopyDirectory(string sourceDir, string destinationDir, int depth)
    {
        if (depth > MaxSeedDepth)
        {
            throw new InvalidOperationException(
                $"Seed source exceeds the maximum directory depth of {MaxSeedDepth}; refusing to copy.");
        }

        foreach (var entry in new DirectoryInfo(sourceDir).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            if (entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                var destSubDir = Path.Combine(destinationDir, entry.Name);
                Directory.CreateDirectory(destSubDir);
                SetContainerAccessiblePermissions(destSubDir);
                CopyDirectory(entry.FullName, destSubDir, depth + 1);
            }
            else
            {
                var destFile = Path.Combine(destinationDir, entry.Name);
                File.Copy(entry.FullName, destFile, overwrite: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(destFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            }
        }
    }

    /// <summary>
    /// Disposes one resource during session teardown, logging rather than throwing on failure —
    /// shared by <see cref="ProcessSandboxSession"/> and <see cref="DockerSandboxSession"/> so
    /// their otherwise-identical teardown paths cannot silently drift apart. <paramref name="context"/>
    /// identifies the session for the log line (a tool name for the container backend, a process
    /// ID for the process backend — whichever the caller already has on hand).
    /// </summary>
    internal static void SafeDispose(IDisposable disposable, string what, string context, ILogger logger)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to dispose {What} during sandbox session teardown for {Context}", what, context);
        }
    }
}
