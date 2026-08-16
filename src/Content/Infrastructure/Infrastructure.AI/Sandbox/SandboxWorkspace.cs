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
}
