namespace Infrastructure.AI.Helpers;

/// <summary>
/// Creates a directory (and any missing parents) with owner-only access on POSIX, for storage roots
/// that hold sensitive content — tool-call payloads, execution traces — and were never meant to be
/// readable by anything beyond the process's own user (#527).
/// </summary>
/// <remarks>
/// Windows is left to its inherited ACL, matching the one other place in this codebase that restricts
/// filesystem permissions for this reason (<c>FileSystemToolResultStore.CreateDirectoryOwnerOnly</c>,
/// #559) — extracted here as a shared helper now that a third consumer
/// (<c>FileSystemExecutionTraceStore</c>) needs the identical behavior, rather than a third
/// hand-copied private method. Deliberately NOT reused by <c>SandboxWorkspace</c>, which grants
/// broader access on purpose (a workspace mounted into a container running as a different UID needs
/// <c>Other</c> permissions) — that is a different security posture for a different reason, not the
/// same gap.
/// </remarks>
internal static class OwnerOnlyDirectoryHelper
{
    /// <summary>
    /// Creates <paramref name="directory"/> (and any missing parents) with owner-only read/write/execute
    /// access on POSIX. A no-op permission-wise if the directory already exists — <see cref="Directory.CreateDirectory(string, UnixFileMode)"/>
    /// only applies the mode to directories it actually creates.
    /// </summary>
    public static void Create(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(
            directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
