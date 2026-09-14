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
/// On Linux/x86_64 this is closed with a direct <c>open(O_NOFOLLOW | O_DIRECTORY)</c> + <c>fchmod</c>
/// pair (<see cref="ApplyOwnerOnlyModeSafely"/>): opening refuses outright (<c>ELOOP</c>) if the path
/// is now a symlink, and the subsequent <c>fchmod</c> targets the already-open descriptor, not the
/// path, so nothing resolved after the open can change what gets chmod'd. <c>O_NOFOLLOW</c>/
/// <c>O_DIRECTORY</c>'s numeric values are NOT the same on every architecture — ARM64/PowerPC define
/// them differently than x86_64/s390x (a real defect caught by <c>correctness</c> review on the first
/// attempt at this fix, which hard-coded the x86_64 values unconditionally: on ARM64 those same bits
/// mean <c>O_LARGEFILE</c>/<c>O_DIRECT</c>, silently disarming the whole protection instead of failing
/// loudly). Rather than hand-type a second set of guessed values for ARM64/PowerPC with no way to
/// verify them on this template's own hosts, the safe path is gated to Linux **x86_64 only** — the
/// architecture of the RUNNING PROCESS, via <c>RuntimeInformation.ProcessArchitecture</c>, not
/// <c>OSArchitecture</c> (a second real defect, caught by <c>/code-review</c> round 3:
/// <c>OSArchitecture</c> reflects the host, and Microsoft's own docs say it does not account for
/// QEMU-based cross-architecture emulation on Linux — exactly how a multi-platform container image
/// can end up running x86_64 code on an ARM64 host or vice versa; <c>ProcessArchitecture</c> reflects
/// what this running process's own code, and therefore its libc calls, actually is). Every other
/// POSIX combination (macOS/BSD, or Linux with a mismatched process architecture) keeps the plain,
/// symlink-following <c>File.SetUnixFileMode</c> call — narrower coverage, but no worse than this
/// helper's behavior before this paragraph's fix, and never silently wrong. Shipping an unverified
/// flag-value guess for a platform this template cannot test would risk silently doing the wrong
/// thing, which is strictly worse than an honest, narrower fallback — the same reasoning
/// <c>HardLinkInspector</c> documents for its own platform coverage.
/// </para>
/// <para>
/// <strong>A compromised segment must stop the whole call, not just itself (#648, round 3)</strong>:
/// the very first two attempts at this fix logged a failed re-assert and moved on to create the NEXT
/// segment anyway. Ordinary path resolution follows a symlink at ANY component of a multi-segment
/// path, not only the exact segment <see cref="ApplyOwnerOnlyModeSafely"/> opens with
/// <c>O_NOFOLLOW</c> — so for the multi-level path essentially every real caller uses, a compromised
/// PARENT segment meant every segment created under it (including the caller's actual target
/// directory) was silently built inside whatever that parent actually resolved to, with only a log
/// line as the trace. <see cref="Create"/> now throws the instant any segment cannot be confirmed
/// owner-only, before creating anything further under it — see its own <c>&lt;exception&gt;</c> doc.
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
    /// Used to report why this call aborted, when it does. <see langword="null"/> is accepted for the
    /// handful of call sites that run before any host container exists to resolve a logger from (see
    /// <c>DependencyInjection.Planner.cs</c>) — those call sites lose observability into why a rare
    /// abort happened, not correctness.
    /// </param>
    /// <exception cref="IOException">
    /// A segment this call is responsible for could not be confirmed as an owner-only directory this
    /// process controls after creating it (/code-review finding, round 3): a concurrent writer deleted
    /// it, symlink-swapped it, or owns it under a different user. Continuing to build further segments
    /// under an unverified parent would silently create them inside whatever that parent actually is —
    /// the exact confidentiality break this whole method exists to prevent — so this call stops
    /// immediately instead of logging and continuing. Any segment already confirmed secure before the
    /// failing one stays on disk, correctly owner-only; nothing below the failure point is created.
    /// </exception>
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
            //
            // ProcessArchitecture, not OSArchitecture (/code-review finding, round 3): OSArchitecture
            // reflects the HOST, and Microsoft's own docs say it does not account for QEMU-based
            // cross-architecture emulation on Linux — exactly how a Docker buildx multi-platform image
            // runs on a mismatched host. ProcessArchitecture reflects what THIS running process's own
            // code (and therefore its libc calls) actually is, which is the only thing that determines
            // whether the flag values below are correct.
            var secured = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? ReassertModeOnLinux(segment, logger)
                : ReassertModeFollowingSymlinks(segment, logger);

            if (!secured)
            {
                // /code-review finding, round 3: logging and continuing here — as every earlier
                // version of this fix did — creates every remaining segment through a parent this
                // call just determined it could NOT confirm as owner-only. Normal path resolution
                // follows a symlink at ANY component, not just the leaf being opened, so continuing
                // would silently build (and let callers write confidential content into) whatever
                // that unverified parent actually is. Stopping here is the only way the re-assert
                // above means anything for a multi-level path — which is every real caller.
                throw new IOException(
                    $"Refusing to create '{fullPath}': could not confirm '{segment}' as an " +
                    "owner-only directory this process controls after creating it (see the " +
                    "preceding log entry for why). Continuing would silently create further " +
                    "directories under a path that could not be verified as secure.");
            }
        }
    }

    /// <summary>
    /// Non-Linux/x86_64 POSIX fallback (macOS, BSD, or Linux on any other architecture): the plain,
    /// symlink-following <c>File.SetUnixFileMode</c>. Returns whether <paramref name="segment"/> is
    /// confirmed owner-only and safe to build further segments under; never throws.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static bool ReassertModeFollowingSymlinks(string segment, ILogger? logger)
    {
        try
        {
            File.SetUnixFileMode(segment, OwnerOnlyMode);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A legitimate concurrent cleanup sweep (e.g. FileSystemToolResultStore's expiry prune,
            // which deletes an empty result directory the instant it observes one) deleted this
            // just-created, still-empty segment in the gap between the create call and this one. If
            // Create() continued past this, its own next CreateDirectory call would silently
            // re-create this segment with the BCL's loose default mode — the exact bug this whole
            // fix exists to close, just via a benign race instead of an adversarial one.
            logger?.LogWarning(ex,
                "Owner-only permission re-assert on {Directory} failed: the directory no longer " +
                "existed, most likely deleted by a concurrent process.", segment);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            // The non-cooperating writer this fix defends against (see the class remarks) won the
            // race running as a DIFFERENT OS user, so this process cannot chmod a directory it does
            // not own — and must not build further segments under a directory it does not control.
            logger?.LogWarning(ex,
                "Could not re-assert owner-only permissions on {Directory}: it is owned by a " +
                "different user than this process, so it cannot be secured here. Something other " +
                "than this application created it — investigate if unexpected.", segment);
            return false;
        }
        catch (IOException ex)
        {
            // /code-review finding, round 3: a plain IOException (e.g. ENOTDIR/EROFS-class chmod
            // failures) was previously left uncaught here — a new crash risk this fix introduced,
            // since no caller of Create() wraps it in a try/catch. Tolerated like the other benign
            // races above, and — like them — reported as "cannot continue" rather than swallowed.
            logger?.LogWarning(ex,
                "Could not re-assert owner-only permissions on {Directory}: {Reason}", segment, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Linux/x86_64 path: re-asserts owner-only mode via <see cref="ApplyOwnerOnlyModeSafely"/>, which
    /// never follows a symlink planted at <paramref name="segment"/> between the create call and this
    /// one. Returns whether <paramref name="segment"/> is confirmed owner-only and safe to build
    /// further segments under; never throws.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static bool ReassertModeOnLinux(string segment, ILogger? logger)
    {
        var (outcome, errno) = ApplyOwnerOnlyModeSafely(segment);
        switch (outcome)
        {
            case ChmodOutcome.Applied:
                return true;

            case ChmodOutcome.SegmentGone:
                // If Create() continued past this, its own next CreateDirectory call would silently
                // re-create this segment with the BCL's loose default mode — the exact bug this whole
                // fix exists to close, just via a benign race instead of an adversarial one.
                logger?.LogWarning(
                    "Owner-only permission re-assert on {Directory} failed: the directory no " +
                    "longer existed, most likely deleted by a concurrent process.", segment);
                return false;

            case ChmodOutcome.NotASafeDirectory:
                // ELOOP or ENOTDIR: the segment stopped being a plain directory between the create
                // call and this one — most plausibly a symlink swap. Refusing to follow it, and
                // refusing to build further segments under it, is the entire point.
                logger?.LogWarning(
                    "Owner-only permission re-assert on {Directory} refused: it is no longer a " +
                    "plain directory (possible symlink swap). Nothing was chmod'd through it. " +
                    "Investigate what replaced it.", segment);
                return false;

            case ChmodOutcome.AccessDenied:
                logger?.LogWarning(
                    "Could not re-assert owner-only permissions on {Directory}: access was denied " +
                    "(likely owned by a different user than this process). Something other than " +
                    "this application created it — investigate if unexpected.", segment);
                return false;

            case ChmodOutcome.OperationFailed:
                logger?.LogWarning(
                    "Could not re-assert owner-only permissions on {Directory}: the operation " +
                    "failed with errno {Errno}, not an ownership problem — investigate separately.",
                    segment, errno);
                return false;

            default:
                return false;
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

        /// <summary>The mode could not be changed because it is owned by a different user (<c>EACCES</c>/<c>EPERM</c>).</summary>
        AccessDenied,

        /// <summary>
        /// <c>open</c>/<c>fchmod</c> failed for a reason other than the above (e.g. <c>EMFILE</c>,
        /// <c>ENOMEM</c>) — not an ownership problem, so logged distinctly rather than folded into
        /// <see cref="AccessDenied"/>'s "owned by a different user" framing (/code-review finding).
        /// </summary>
        OperationFailed,
    }

    /// <summary>
    /// Applies <see cref="OwnerOnlyMode"/> to <paramref name="segment"/> via <c>fchmod</c> on a
    /// descriptor opened with <c>O_NOFOLLOW | O_DIRECTORY</c>, so a symlink or non-directory planted
    /// at that exact path between this call's own create and this call is refused rather than
    /// followed — see the class remarks for the race this closes. Never throws.
    /// </summary>
    /// <remarks>
    /// Only ever called for Linux/x86_64 (gated in <see cref="Create"/>) — the flag values below are
    /// specific to that architecture family. See the class remarks for why no other architecture is
    /// guessed at.
    /// </remarks>
    [SupportedOSPlatform("linux")]
    private static (ChmodOutcome Outcome, int Errno) ApplyOwnerOnlyModeSafely(string segment)
    {
        const int oRdOnly = 0;
        const int oNoFollow = 0x20000;   // O_NOFOLLOW (Linux/x86_64 and s390x; NOT ARM64/PowerPC)
        const int oDirectory = 0x10000;  // O_DIRECTORY (Linux/x86_64 and s390x; NOT ARM64/PowerPC)
        const int enoent = 2;
        const int eacces = 13;
        const int eperm = 1;
        const int eloop = 40;
        const int enotdir = 20;

        var fd = Open(segment, oRdOnly | oNoFollow | oDirectory);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            var outcome = errno switch
            {
                enoent => ChmodOutcome.SegmentGone,
                eloop or enotdir => ChmodOutcome.NotASafeDirectory,
                eacces or eperm => ChmodOutcome.AccessDenied,
                // Anything unrecognized (EMFILE, ENOMEM, ...): never guess a success, but never
                // mislabel it as an ownership problem either — that sends an investigator the wrong
                // direction (/code-review finding).
                _ => ChmodOutcome.OperationFailed,
            };
            return (outcome, errno);
        }

        // Wrapping the already-known fd guarantees close() runs even if FChmod throws (it doesn't,
        // but this keeps the descriptor lifetime unconditional rather than relying on that).
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        if (FChmod(fd, (int)OwnerOnlyMode) == 0)
            return (ChmodOutcome.Applied, 0);

        var chmodErrno = Marshal.GetLastPInvokeError();
        return (chmodErrno is eacces or eperm ? ChmodOutcome.AccessDenied : ChmodOutcome.OperationFailed, chmodErrno);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int FChmod(int fd, int mode);
}
