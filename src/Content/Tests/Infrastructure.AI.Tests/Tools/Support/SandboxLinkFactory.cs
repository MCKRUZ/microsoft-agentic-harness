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
    /// hard link, which is a different attack with a different defence (the per-operation link-count
    /// check, not link resolution; see <see cref="CreateHardLink"/>) — so this skips when symlinks
    /// are unavailable.
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

    /// <summary>
    /// Creates a hard link at <paramref name="link"/> naming the same file as
    /// <paramref name="target"/>, skipping the calling test when the host forbids it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no managed API for this, so both platforms shell out to the same unprivileged
    /// command an attacker would use: <c>mklink /H</c> on Windows, <c>ln</c> everywhere else.
    /// Neither needs elevation, neither needs Developer Mode, and the resulting entry carries no
    /// reparse point — which is exactly why the sandbox's path canonicalization cannot see it.
    /// </para>
    /// <para>
    /// The link and target must be on the same volume, which every caller satisfies by putting
    /// both under one temporary root. That is not a limitation of the test: it is the shipped
    /// default's geometry, where <c>workspace</c> and <c>.agent-state</c> are siblings.
    /// </para>
    /// </remarks>
    /// <param name="link">The hard-link path to create; must not already exist.</param>
    /// <param name="target">The existing file to create a second directory entry for.</param>
    public static void CreateHardLink(string link, string target)
    {
        var failure = OperatingSystem.IsWindows()
            ? RunLinkCommand("cmd.exe", ["/c", "mklink", "/H", link, target])
            : RunLinkCommand("/bin/ln", [target, link]);

        Skip.If(
            failure is not null || !File.Exists(link),
            $"This host does not permit creating hard links: {failure ?? "the link was not created"}.");
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
        var failure = RunLinkCommand("cmd.exe", ["/c", "mklink", "/J", link, target]);
        if (failure is not null)
            return failure;

        return Directory.Exists(link) ? null : "mklink created no junction";
    }

    /// <summary>
    /// Runs a link-creating command to completion. Returns <see langword="null"/> when it exited
    /// zero, or a short human-readable reason otherwise.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessStartInfo.ArgumentList"/> rather than a joined argument string: it quotes
    /// each entry, so the temporary paths these tests build survive spaces intact.
    /// </remarks>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">Arguments, one entry per argument.</param>
    private static string? RunLinkCommand(string fileName, string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return $"{fileName} did not start";

            if (!process.WaitForExit(MkLinkTimeout))
            {
                process.Kill(entireProcessTree: true);
                return $"{fileName} timed out";
            }

            return process.ExitCode == 0 ? null : $"{fileName} exited {process.ExitCode}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.GetType().Name;
        }
    }
}
