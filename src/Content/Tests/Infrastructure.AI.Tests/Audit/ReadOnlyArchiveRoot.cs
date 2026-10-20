using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Infrastructure.AI.Audit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// An archive root as the application sees it (#416): marked ready by the archiver, and refusing to let anyone create a
/// file directly in it, as a read-only mount would. Its chain folders, created up front, stay writable so a test can play
/// the archiver's part.
/// </summary>
/// <remarks>
/// Windows: a deny rule for creating files and folders on the root only, not inherited. Linux and macOS: mode 0555. Root
/// ignores modes, so on a Unix test host running as root the root stays writable and the write probe would fail every
/// test; CI runs as an ordinary user.
/// </remarks>
internal static class ReadOnlyArchiveRoot
{
    /// <summary>Creates the root with the archiver's marker file and the named chain folders, then makes the root refuse new files.</summary>
    /// <param name="root">The archive root.</param>
    /// <param name="chainFolders">Chain folders to create while the root still accepts them.</param>
    public static void Create(string root, params string[] chainFolders)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, HashChainedJsonlWriter.ArchiveSentinelFileName), "ready\n");
        foreach (var folder in chainFolders)
            Directory.CreateDirectory(Path.Combine(root, folder));
        Lock(root);
    }

    /// <summary>Makes <paramref name="root"/> refuse new files.</summary>
    /// <param name="root">The folder.</param>
    public static void Lock(string root)
    {
        if (OperatingSystem.IsWindows())
            SetDenyCreate(root, add: true);
        else
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>Lets <paramref name="root"/> accept new files again (so a test can clean up or misconfigure it).</summary>
    /// <param name="root">The folder.</param>
    public static void Unlock(string root)
    {
        if (!Directory.Exists(root))
            return;

        if (OperatingSystem.IsWindows())
            SetDenyCreate(root, add: false);
        else
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void SetDenyCreate(string root, bool add)
    {
        var directory = new DirectoryInfo(root);
        var security = directory.GetAccessControl();
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
        if (add)
            security.AddAccessRule(rule);
        else
            security.RemoveAccessRuleSpecific(rule);
        directory.SetAccessControl(security);
    }
}
