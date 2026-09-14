using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Infrastructure.AI.Helpers;

/// <summary>
/// Creates a directory (and any missing parents) with owner-only access on POSIX, for storage roots
/// that hold sensitive content — tool-call payloads, execution traces — and were never meant to be
/// readable by anything beyond the process's own user (#527).
/// </summary>
/// <remarks>
/// Windows is left to its inherited ACL. Deliberately NOT reused by <c>SandboxWorkspace</c>, which
/// grants broader access on purpose (a workspace mounted into a container running as a different UID
/// needs <c>Other</c> permissions) — that is a different security posture for a different reason, not
/// the same gap.
/// <para>
/// <strong>Does not remediate a directory that already exists</strong> (/code-review, round 2) — this
/// only tightens permissions on the create path. A host upgraded in place, whose storage root was
/// already created by pre-#527 code with looser default permissions, keeps that root's old mode
/// indefinitely; only the NEW leaf directories created under it after the upgrade get owner-only.
/// Closing that gap needs a one-time startup remediation pass — tracked in #670.
/// </para>
/// <para>
/// <strong>A non-cooperating writer racing to create the same new segment first (#648)</strong> no
/// longer wins permanently: every segment this call determines is missing gets its mode re-asserted
/// after the create call, not just trusted from the create call's mode argument. That argument is
/// silently ignored by the BCL whenever the segment already exists by the time this call's own create
/// runs — which, empirically (verified via a real concurrent-racer control run against the pre-fix
/// code), can only happen because of a writer that reached the segment through something OTHER than a
/// cooperating call to this same method: two callers of <see cref="Create"/> racing each other can
/// never leave a segment at the loose default, because every caller requests the identical owner-only
/// mode and <c>Directory.CreateDirectory</c> applies the WINNING caller's requested mode atomically at
/// creation. The re-assert exists for the writer that does NOT request that mode — e.g. plain
/// <c>Directory.CreateDirectory(path)</c> elsewhere in the codebase, or a future caller that forgets
/// to route through this helper.
/// </para>
/// <para>
/// <strong>The re-assert step has its own, narrower TOCTOU window on Linux (#648, round 2)</strong>:
/// the BCL's <c>File.SetUnixFileMode</c> follows symlinks (no <c>lchmod</c> equivalent exists on
/// Linux), so a writer with access to the same parent directory could, in the gap between this call's
/// create and its permission re-assert, delete the just-created segment and replace it with a symlink
/// to an attacker-owned directory — turning the re-assert into an attacker-controlled <c>chmod</c>.
/// On Linux this is closed with a direct <c>open(O_NOFOLLOW | O_DIRECTORY)</c> + <c>fchmod</c> pair
/// (<see cref="ApplyOwnerOnlyModeSafely"/>): opening refuses outright (<c>ELOOP</c>) if the path is
/// now a symlink, and the subsequent <c>fchmod</c> targets the already-open descriptor, not the path,
/// so nothing resolved after the open can change what gets chmod'd. Every other POSIX platform (only
/// macOS/BSD, since Windows never reaches this code path) keeps the plain, symlink-following
/// <c>File.SetUnixFileMode</c> call — narrower coverage than Linux, but no worse than this helper's
/// behavior before this paragraph's fix. Shipping an unverified <c>open</c>/<c>fchmod</c> flag-value
/// guess for a platform this template cannot test would risk silently doing the wrong thing, which is
/// strictly worse than an honest, narrower fallback — the same reasoning <c>HardLinkInspector</c>
/// documents for its own platform coverage.
/// </para>
/// </remarks>
internal static class OwnerOnlyDirectoryHelper
{
    private const UnixFileMode OwnerOnlyMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Test-only seam: when set, invoked with each segment immediately before this method's own
    /// creation attempt for it, letting a test deterministically simulate a concurrent, non-
    /// cooperating writer creating that exact segment first — instead of relying on real thread
    /// scheduling to land in a narrow timing window. Always <see langword="null"/> in production.
    /// </summary>
    internal static Action<string>? RaceSimulationHookForTests;

    /// <summary>
    /// Creates <paramref name="directory"/> (and any missing parents) with owner-only read/write/execute
    /// access on POSIX, applied to <em>every</em> directory this call actually creates — not just the
    /// leaf. A no-op permission-wise for any segment that already exists.
    /// </summary>
    /// <param name="directory">The directory to create.</param>
    /// <param name="logger">
    /// Used only to report the benign races this call tolerates instead of throwing (a concurrent
    /// cleanup sweep deleting a just-created segment, or a non-cooperating writer racing to create or
    /// even symlink-swap a segment this call cannot secure). <see langword="null"/> is accepted for
    /// the handful of call sites that run before any host container exists to resolve a logger from
    /// (see <c>DependencyInjection.Planner.cs</c>) — those call sites lose observability into this
    /// method's rare failure paths, not correctness.
    /// </param>
    /// <remarks>
    /// <c>Directory.CreateDirectory(path, mode)</c>'s single-call overload does NOT do this
    /// (/code-review finding, verified against <c>dotnet/runtime</c>'s <c>FileSystem.Unix.cs</c>):
    /// its internal <c>CreateParentsAndDirectory</c> only applies the caller's requested mode to the
    /// final path segment — every missing intermediate directory it creates along the way gets the
    /// BCL's loose default (0777 minus umask, typically 0755). On a first-ever call for a not-yet-
    /// existing storage root, that would leave every intermediate segment (e.g. the trace root, an
    /// "executions" folder, an execution-run-id folder) world-readable/executable — permanently, since
    /// nothing revisits their permissions once created — even though the leaf directory this method
    /// was actually asked to protect ends up correctly restricted. Walking the path manually and
    /// creating each missing segment with its own single-directory call (root-to-leaf, so a segment's
    /// parent always already exists by the time that segment's own call runs) is what closes this: each
    /// such call's "final path segment" is always the one directory it is responsible for, so the
    /// requested mode lands on every level, not just the last one.
    /// </remarks>
    public static void Create(string directory, ILogger? logger = null)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        var fullPath = Path.GetFullPath(directory);
        var missingSegments = new Stack<string>();
        var current = fullPath;
        while (!Directory.Exists(current))
        {
            missingSegments.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
                break;
            current = parent;
        }

        while (missingSegments.Count > 0)
        {
            var segment = missingSegments.Pop();
            RaceSimulationHookForTests?.Invoke(segment);
            Directory.CreateDirectory(segment, OwnerOnlyMode);

            // #648: a non-cooperating writer can win the race to create this exact segment first,
            // with the BCL's loose default mode — CreateDirectory is then a silent no-op for
            // permissions on an already-existing directory, so the mode argument above is not a
            // guarantee. Re-asserting the mode here, unconditionally, closes that.
            if (OperatingSystem.IsLinux())
                ReassertModeOnLinux(segment, logger);
            else
                ReassertModeFollowingSymlinks(segment, logger);
        }
    }

    /// <summary>
    /// Non-Linux POSIX fallback (macOS/BSD): the plain, symlink-following <c>File.SetUnixFileMode</c>,
    /// tolerating the same two benign races <see cref="ReassertModeOnLinux"/> tolerates, now logged
    /// instead of silently swallowed.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static void ReassertModeFollowingSymlinks(string segment, ILogger? logger)
    {
        try
        {
            File.SetUnixFileMode(segment, OwnerOnlyMode);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A legitimate concurrent cleanup sweep (e.g. FileSystemToolResultStore's expiry prune,
            // which deletes an empty result directory the instant it observes one) deleted this
            // just-created, still-empty segment in the gap between the create call and this one.
            // Nothing is left to protect at that point — not a failure of this call's job.
            logger?.LogDebug(ex,
                "Owner-only permission re-assert on {Directory} skipped: the directory no longer " +
                "existed, most likely deleted by a concurrent cleanup sweep.", segment);
        }
        catch (UnauthorizedAccessException ex)
        {
            // The non-cooperating writer this fix defends against (see the class remarks) won the
            // race running as a DIFFERENT OS user, so this process cannot chmod a directory it does
            // not own. Throwing here would make the adversarial case this fix targets crash instead
            // of just leaving the pre-existing, already-undefended permission gap.
            logger?.LogWarning(ex,
                "Could not re-assert owner-only permissions on {Directory}: it is owned by a " +
                "different user than this process, so it cannot be secured here. Something other " +
                "than this application created it — investigate if unexpected.", segment);
        }
    }

    /// <summary>
    /// Linux path: re-asserts owner-only mode via <see cref="ApplyOwnerOnlyModeSafely"/>, which never
    /// follows a symlink planted at <paramref name="segment"/> between the create call and this one.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static void ReassertModeOnLinux(string segment, ILogger? logger)
    {
        switch (ApplyOwnerOnlyModeSafely(segment))
        {
            case ChmodOutcome.Applied:
                break;

            case ChmodOutcome.SegmentGone:
                logger?.LogDebug(
                    "Owner-only permission re-assert on {Directory} skipped: the directory no " +
                    "longer existed, most likely deleted by a concurrent cleanup sweep.", segment);
                break;

            case ChmodOutcome.NotASafeDirectory:
                // ELOOP or ENOTDIR: the segment stopped being a plain directory between the create
                // call and this one — most plausibly a symlink swap. Refusing to follow it is the
                // entire point; this is the case worth the loudest signal.
                logger?.LogWarning(
                    "Owner-only permission re-assert on {Directory} refused: it is no longer a " +
                    "plain directory (possible symlink swap). Nothing was chmod'd through it. " +
                    "Investigate what replaced it.", segment);
                break;

            case ChmodOutcome.AccessDenied:
                logger?.LogWarning(
                    "Could not re-assert owner-only permissions on {Directory}: access was denied " +
                    "(likely owned by a different user than this process). Something other than " +
                    "this application created it — investigate if unexpected.", segment);
                break;
        }
    }

    /// <summary>Outcome of <see cref="ApplyOwnerOnlyModeSafely"/>.</summary>
    private enum ChmodOutcome
    {
        /// <summary>The mode was applied to the directory this call created (or found already there).</summary>
        Applied,

        /// <summary>The segment no longer existed (<c>ENOENT</c>) — a benign concurrent delete.</summary>
        SegmentGone,

        /// <summary>
        /// The segment is no longer a plain directory (<c>ELOOP</c> — a symlink — or <c>ENOTDIR</c>) —
        /// refused rather than followed.
        /// </summary>
        NotASafeDirectory,

        /// <summary>The mode could not be changed (<c>EACCES</c>/<c>EPERM</c>, or an unrecognized errno).</summary>
        AccessDenied,
    }

    /// <summary>
    /// Applies <see cref="OwnerOnlyMode"/> to <paramref name="segment"/> via <c>fchmod</c> on a
    /// descriptor opened with <c>O_NOFOLLOW | O_DIRECTORY</c>, so a symlink or non-directory planted
    /// at that exact path between this call's own create and this call is refused rather than
    /// followed — see the class remarks for the race this closes. Never throws.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static ChmodOutcome ApplyOwnerOnlyModeSafely(string segment)
    {
        const int oRdOnly = 0;
        const int oNoFollow = 0x20000;   // O_NOFOLLOW (Linux, all supported architectures)
        const int oDirectory = 0x10000;  // O_DIRECTORY (Linux, all supported architectures)
        const int enoent = 2;
        const int eloop = 40;
        const int enotdir = 20;

        var fd = Open(segment, oRdOnly | oNoFollow | oDirectory);
        if (fd < 0)
        {
            return Marshal.GetLastPInvokeError() switch
            {
                enoent => ChmodOutcome.SegmentGone,
                eloop or enotdir => ChmodOutcome.NotASafeDirectory,
                // EACCES/EPERM, or anything unrecognized: never guess a success. A directory whose
                // mode could not be verified as changed must be reported as not secured.
                _ => ChmodOutcome.AccessDenied,
            };
        }

        // Wrapping the already-known fd guarantees close() runs even if FChmod throws (it doesn't,
        // but this keeps the descriptor lifetime unconditional rather than relying on that).
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        return FChmod(fd, (int)OwnerOnlyMode) == 0 ? ChmodOutcome.Applied : ChmodOutcome.AccessDenied;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int FChmod(int fd, int mode);
}
