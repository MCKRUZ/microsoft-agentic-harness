using System.Diagnostics;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Creates the filesystem links the sandbox-escape tests need, using the same primitives an
/// attacker would, and skips the calling test rather than passing vacuously when the host forbids
/// every one of them.
/// </summary>
/// <remarks>
/// Two mechanisms are tried in order, because their privilege requirements differ and the point of
/// these tests is that the attack needs no privilege. <see cref="Directory.CreateSymbolicLink"/>
/// works freely on Linux but needs Developer Mode or elevation on Windows; a Windows directory
/// junction (<c>mklink /J</c>) needs neither, which is precisely why it is the more reachable
/// attack. Falling back to the junction keeps the test real on a stock Windows agent instead of
/// silently skipping there.
/// </remarks>
internal static class SandboxLinkFactory
{
    private static readonly TimeSpan MkLinkTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a directory link at <paramref name="link"/> resolving to <paramref name="target"/>.
    /// </summary>
    /// <param name="link">The link path to create; must not already exist.</param>
    /// <param name="target">The existing directory the link resolves to.</param>
    public static void CreateDirectoryLink(string link, string target)
    {
        if (TryCreateSymbolicLink(link, target, out var symlinkFailure))
            return;

        if (!OperatingSystem.IsWindows())
        {
            Skip.If(true, $"This host does not permit creating directory symlinks: {symlinkFailure}");
            return;
        }

        var junctionFailure = TryCreateJunction(link, target);
        Skip.If(
            junctionFailure is not null,
            $"This host permits neither directory symlinks ({symlinkFailure}) nor junctions ({junctionFailure}).");
    }

    /// <summary>
    /// Creates a file link at <paramref name="link"/> resolving to <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// There is no unprivileged Windows fallback for a file symlink — <c>mklink /H</c> creates a
    /// hard link, which is a different attack with a different defence (the boot-time containment
    /// assertion, not link resolution) — so this skips when symlinks are unavailable.
    /// </remarks>
    /// <param name="link">The link path to create; must not already exist.</param>
    /// <param name="target">The existing file the link resolves to.</param>
    public static void CreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            Skip.If(true, $"This host does not permit creating file symlinks: {ex.GetType().Name}");
        }
    }

    private static bool TryCreateSymbolicLink(string link, string target, out string? failure)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            failure = ex.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// Runs <c>cmd /c mklink /J</c>. Returns <see langword="null"/> on success, or a short reason.
    /// </summary>
    private static string? TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                // ArgumentList quotes each entry, so paths with spaces survive intact.
                ArgumentList = { "/c", "mklink", "/J", link, target },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
                return "cmd.exe did not start";

            if (!process.WaitForExit(MkLinkTimeout))
            {
                process.Kill(entireProcessTree: true);
                return "mklink timed out";
            }

            return Directory.Exists(link) ? null : $"mklink exited {process.ExitCode}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.GetType().Name;
        }
    }
}
