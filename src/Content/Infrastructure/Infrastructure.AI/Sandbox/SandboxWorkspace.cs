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
