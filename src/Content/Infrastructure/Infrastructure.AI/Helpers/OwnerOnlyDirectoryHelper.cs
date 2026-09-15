using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

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
/// <strong>A host upgraded in place is remediated retroactively, not just from the next fresh
/// create (#670)</strong>: <see cref="Create"/>'s originally-shipped behavior (/code-review, round 2)
/// only tightened permissions on the create path — a storage root already created by pre-#527 code
/// with the BCL's loose default mode kept that mode indefinitely, since nothing ever revisited a
/// segment the call did not itself create. <see cref="Create"/> now checks whether the exact directory
/// it was asked to secure already exists and, if so, reasserts its mode the same way a freshly-created
/// segment gets reasserted — no separate startup pass or hand-maintained list of storage roots to keep
/// in sync required, since it runs inside the same call every one of this helper's ~20 existing call
/// sites already makes. An already-existing ANCESTOR encountered while walking up to find a DIFFERENT
/// (missing) leaf is deliberately left untouched, exactly as before — an ancestor may be shared by
/// something outside this call's control, which is not true of the leaf a caller explicitly asked this
/// helper to own.
/// </para>
/// <para>
/// <strong>Two known gaps tracked separately (#682), not fixed here.</strong> First: nothing bounds
/// which paths this retroactive reassert may touch, so a config typo pointing a storage root at an
/// already-existing shared/system directory now actively <c>chmod</c>s it instead of being a harmless
/// no-op — closing this needs a bounded, non-brittle guard, not a hard-coded deny-list. Second: several
/// of this helper's ~20 call sites invoke <see cref="Create"/> completely unguarded, and a pre-existing
/// directory genuinely owned by a different user (a real, non-adversarial scenario: a bind-mounted
/// volume created by root, with the app running as a non-root user) now throws where it previously
/// could not — each such call site needs its own reviewed decision about acceptable degraded behavior,
/// which #682 tracks as its own scoped follow-up.
/// </para>
/// <para>
/// <strong>Retroactively reasserting on every call, not just once per process, is deliberate — do NOT
/// "optimize" it into a per-path cache of already-verified directories</strong> (efficiency review,
/// #670 follow-up). The real cost is real: several call sites reach this on a hot path (e.g.
/// <c>HashChainedJsonlWriter.AppendAsync</c>, under its own serializing semaphore, on every audit
/// record). But repeated verification on every access is exactly what makes the TOCTOU protection in
/// the next two paragraphs mean anything for a long-running process: an attacker with access to the
/// same parent directory could wait until AFTER this call's first successful verification, then swap
/// the directory for a symlink — a cache that skips re-verification for a path already seen this
/// process lifetime would silently stop catching that for every call after the first. If the syscall
/// cost on a specific hot path ever needs addressing, the fix belongs at that call site (e.g. caching
/// whether creation is even needed before calling <see cref="Create"/> at all), not inside this method.
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
/// On supported Linux architectures this is closed with a direct
/// <c>open(O_NOFOLLOW | O_DIRECTORY)</c> + <c>fchmod</c> pair
/// (<see cref="ApplyOwnerOnlyModeSafely"/>): opening refuses outright (<c>ELOOP</c>) if the path is
/// now a symlink, and the subsequent <c>fchmod</c> targets the already-open descriptor, not the path,
/// so nothing resolved after the open can change what gets chmod'd. <c>O_NOFOLLOW</c>/
/// <c>O_DIRECTORY</c>'s numeric values are NOT the same on every architecture (a real defect caught
/// by <c>correctness</c> review on the first attempt at this fix, which hard-coded the x86_64 values
/// unconditionally: on ARM64 those same bits mean <c>O_LARGEFILE</c>/<c>O_DIRECT</c>, silently
/// disarming the whole protection instead of failing loudly) — <see cref="GetSafeReassertFlags"/>
/// maps <see cref="Architecture"/> to the correct pair, verified directly against the Linux kernel's
/// own <c>arch/*/include/uapi/asm/fcntl.h</c> / <c>include/uapi/asm-generic/fcntl.h</c> sources (#677),
/// not glibc docs or memory: x86_64 and s390 define no architecture-specific override and fall
/// through to the generic header's <c>O_DIRECTORY=(1&lt;&lt;16)</c>/<c>O_NOFOLLOW=(1&lt;&lt;17)</c>,
/// while ARM64 and PowerPC (ppc64le) each define their OWN override —
/// <c>O_DIRECTORY=(1&lt;&lt;14)</c>/<c>O_NOFOLLOW=(1&lt;&lt;15)</c> — before including the generic
/// header, whose include guards then skip redefining them. Architecture is read via
/// <c>RuntimeInformation.ProcessArchitecture</c>, not <c>OSArchitecture</c> (a second real defect,
/// caught by <c>/code-review</c> round 3: <c>OSArchitecture</c> reflects the host, and Microsoft's own
/// docs say it does not account for QEMU-based cross-architecture emulation on Linux — exactly how a
/// multi-platform container image can end up running x86_64 code on an ARM64 host or vice versa;
/// <c>ProcessArchitecture</c> reflects what this running process's own code, and therefore its libc
/// calls, actually is). Any architecture <see cref="GetSafeReassertFlags"/> does not recognize (e.g.
/// 32-bit ARM/PowerPC, RISC-V) keeps the plain, symlink-following <c>File.SetUnixFileMode</c> call,
/// now preceded by its own explicit (check-then-act, not TOCTOU-proof) symlink check —
/// see <see cref="ReassertModeFollowingSymlinks"/>'s own remarks for why #670 made that check
/// necessary where it previously was not. Shipping a guessed flag value for an architecture this
/// template cannot verify against the kernel's own headers would risk silently doing the wrong thing,
/// which is strictly worse than an honest, narrower fallback — the same reasoning
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
internal static partial class OwnerOnlyDirectoryHelper
{
    private const UnixFileMode OwnerOnlyMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Test-only seam: invoked with each segment immediately before <see cref="Create"/>'s own
    /// create-then-reassert step for it — a genuinely missing segment's create call is real; a segment
    /// that already existed before this call ran (#670) hits the identical call, but
    /// <c>Directory.CreateDirectory</c> no-ops on it, so only the reassert that follows does real work.
    /// Either way, this lets a test deterministically simulate a concurrent writer having reached that
    /// exact segment, instead of relying on real thread scheduling to land in a narrow timing window.
    /// Always unset in production.
    /// </summary>
    /// <remarks>
    /// <see cref="AsyncLocal{T}"/>, not a plain shared <see langword="static"/> field (/simplify
    /// finding on #676's first cut, which instead isolated the one consumer test class into a
    /// dedicated xUnit collection): a plain static field is visible process-wide, so a test that sets
    /// it could transiently leak into any OTHER test calling <see cref="Create"/> concurrently under
    /// xUnit's default cross-class parallelization — fixable per-consumer with a collection, but only
    /// for the consumers that remember to join it. Scoping to the logical call context instead removes
    /// the cross-test visibility problem structurally, for every current and future test that sets it,
    /// with nothing to opt into.
    /// </remarks>
    internal static readonly AsyncLocal<Action<string>?> RaceSimulationHookForTests = new();

    /// <summary>
    /// Creates <paramref name="directory"/> (and any missing parents) with owner-only read/write/execute
    /// access on POSIX, applied to <em>every</em> directory this call actually creates — not just the
    /// leaf. If <paramref name="directory"/> itself already exists, its mode is retroactively reasserted
    /// too (#670) — an already-existing ANCESTOR encountered while walking up to find the first missing
    /// segment is left untouched, the same as always.
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
    /// process controls (/code-review finding, round 3): a concurrent writer deleted it, symlink-swapped
    /// it, or owns it under a different user. Continuing to build further segments under an unverified
    /// parent would silently create them inside whatever that parent actually is — the exact
    /// confidentiality break this whole method exists to prevent — so this call stops immediately
    /// instead of logging and continuing. Any segment already confirmed secure before the failing one
    /// stays on disk, correctly owner-only; nothing below the failure point is created.
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

        // #670: fullPath itself already existed — a host upgraded in place may have created this
        // exact directory before this helper existed, with the BCL's loose default mode. Pushing it
        // explicitly here (rather than special-casing "nothing missing" as "nothing to do") reuses
        // the exact same create-then-reassert step every genuinely missing segment already gets below:
        // Directory.CreateDirectory is a documented no-op on an already-existing path (see this
        // method's own <remarks>), so the only real work this adds for fullPath is the retroactive
        // reassert itself (/simplify finding: this also removes a second, redundant Directory.Exists
        // stat the previous separate already-exists branch paid on top of this same walk's own first
        // check). An already-existing ANCESTOR found while walking up for a genuinely missing leaf is
        // different and stays untouched below, exactly as before — it is never pushed here, only
        // fullPath is, and only when nothing else was missing.
        if (missingSegments.Count == 0)
            missingSegments.Push(fullPath);

        while (missingSegments.Count > 0)
        {
            var segment = missingSegments.Pop();
            RaceSimulationHookForTests.Value?.Invoke(segment);
            Directory.CreateDirectory(segment, OwnerOnlyMode);

            // #648: a non-cooperating writer can win the race to create this exact segment first,
            // with the BCL's loose default mode — CreateDirectory is then a silent no-op for
            // permissions on an already-existing directory, so the mode argument above is not a
            // guarantee. Re-asserting the mode here, unconditionally, closes that — and, since #670,
            // is also what retroactively secures a segment that already existed before this call ran.
            ReassertOrThrow(fullPath, segment, logger);
        }
    }

    /// <summary>
    /// Re-asserts owner-only mode on <paramref name="segment"/> and throws if it cannot be confirmed
    /// secure — shared by <see cref="Create"/>'s missing-segment walk and its #670 already-exists case,
    /// so both report the identical failure shape.
    /// </summary>
    /// <remarks>
    /// Only ever called from <see cref="Create"/>, after its own <c>OperatingSystem.IsWindows()</c>
    /// early return — the <see cref="UnsupportedOSPlatformAttribute"/> below documents that for
    /// callers/analyzers, since the guard lives in the caller, not in this method itself.
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    private static void ReassertOrThrow(string fullPath, string segment, ILogger? logger)
    {
        // ProcessArchitecture, not OSArchitecture (/code-review finding, round 3): OSArchitecture
        // reflects the HOST, and Microsoft's own docs say it does not account for QEMU-based
        // cross-architecture emulation on Linux — exactly how a Docker buildx multi-platform image
        // runs on a mismatched host. ProcessArchitecture reflects what THIS running process's own
        // code (and therefore its libc calls) actually is, which is the only thing that determines
        // whether the open() flag values are correct (#677).
        var secured = OperatingSystem.IsLinux() && GetSafeReassertFlags(RuntimeInformation.ProcessArchitecture) is { } flags
            ? ReassertModeOnLinux(segment, flags.NoFollow, flags.Directory, logger)
            : ReassertModeFollowingSymlinks(segment, logger);

        if (secured)
            return;

        // /code-review finding, round 3: logging and continuing here — as every earlier version of
        // this fix did — creates every remaining segment through a parent this call just determined
        // it could NOT confirm as owner-only. Normal path resolution follows a symlink at ANY
        // component, not just the leaf being opened, so continuing would silently build (and let
        // callers write confidential content into) whatever that unverified parent actually is.
        // Stopping here is the only way the re-assert above means anything for a multi-level path —
        // which is every real caller.
        //
        // The "preceding log entry" pointer is conditional on logger being non-null (/code-review
        // finding, #671/#672/#673): FileLoggerProvider and StructuredJsonLoggerProvider deliberately
        // call Create with no logger, to avoid an ILoggerFactory construction cycle (see
        // IOwnerOnlyDirectoryCreator's own remarks) — a hardcoded pointer to a log entry that can
        // never exist for those two callers would send an investigator looking for something that
        // was never written.
        var detail = logger is not null
            ? "(see the preceding log entry for why)"
            : "(no logger was supplied to this call; see the reassert failure reason, if " +
              "available, in whatever caught this exception)";

        // /code-review finding: the "would silently create further directories" wording only makes
        // sense for the missing-segment walk. When segment == fullPath, this is the #670 already-exists
        // case — a single pre-existing leaf, not a multi-level build in progress — and the old wording
        // sent an investigator looking for a partially-built tree that never existed.
        var consequence = segment == fullPath
            ? "This directory already existed and could not be confirmed as one this process " +
              "exclusively controls; nothing was created or modified under it."
            : "Continuing would silently create further directories under a path that could not be " +
              "verified as secure.";
        throw new IOException(
            $"Refusing to secure '{fullPath}': could not confirm '{segment}' as an " +
            $"owner-only directory this process controls {detail}. {consequence}");
    }

    /// <summary>
    /// Shared wording for a segment that vanished between this call's create and its reassert —
    /// used by both platform paths so the same failure reads identically in the logs regardless of
    /// which one produced it (/simplify finding: the two paths had drifted to near-duplicate text).
    /// </summary>
    private static void LogSegmentGone(ILogger? logger, string segment, Exception? exception = null) =>
        logger?.LogWarning(exception,
            "Owner-only permission re-assert on {Directory} failed: the directory no longer " +
            "existed, most likely deleted by a concurrent process.", segment);

    /// <summary>
    /// Shared wording for a segment owned by a different user than this process — see
    /// <see cref="LogSegmentGone"/> for why this is factored out.
    /// </summary>
    private static void LogAccessDenied(ILogger? logger, string segment, Exception? exception = null) =>
        logger?.LogWarning(exception,
            "Could not re-assert owner-only permissions on {Directory}: it is owned by a " +
            "different user than this process, so it cannot be secured here. Something other " +
            "than this application created it — investigate if unexpected.", segment);

    /// <summary>
    /// Shared wording for a segment that turned out not to be a plain directory this call can safely
    /// chmod (a symlink swap or non-directory) — used by both platform paths, same reason as
    /// <see cref="LogSegmentGone"/> (/simplify finding: the symlink check added to
    /// <see cref="ReassertModeFollowingSymlinks"/> for #670 had grown its own near-duplicate of the
    /// wording <see cref="ReassertModeOnLinux"/>'s <c>NotASafeDirectory</c> case already used —
    /// reproducing the exact drift this factoring already exists to prevent).
    /// </summary>
    private static void LogNotASafeDirectory(ILogger? logger, string segment) =>
        logger?.LogWarning(
            "Owner-only permission re-assert on {Directory} refused: it is no longer a plain " +
            "directory (possible symlink swap). Nothing was chmod'd through it. Investigate what " +
            "replaced it.", segment);

    /// <summary>
    /// Non-Linux/x86_64 POSIX fallback (macOS, BSD, or Linux on any other architecture): the plain,
    /// symlink-following <c>File.SetUnixFileMode</c>, preceded by an explicit symlink check. Returns
    /// whether <paramref name="segment"/> is confirmed owner-only and safe to build further segments
    /// under; never throws.
    /// </summary>
    /// <remarks>
    /// <strong>The symlink check is new (#670, security-review finding).</strong> Before #670, a path
    /// this helper did not itself create was never touched — a symlink planted at leisure at a
    /// pre-existing storage root, with no race required, would simply sit there inert. #670's
    /// retroactive-remediation branch (see <see cref="Create"/>) now reaches that same pre-existing
    /// path with a real <c>chmod</c>, and on this fallback platform family <c>File.SetUnixFileMode</c>
    /// follows symlinks — so without this check, a standing (not merely race-won) symlink plant would
    /// be silently followed, applying owner-only mode to whatever the attacker's link actually points
    /// at and reporting success. This check is still check-then-act (a symlink could theoretically be
    /// swapped in between this check and the <c>SetUnixFileMode</c> call below), so it closes the
    /// deterministic, no-race-needed case #670 introduced — it does not claim the same TOCTOU-proof
    /// guarantee <see cref="ApplyOwnerOnlyModeSafely"/>'s single <c>open(O_NOFOLLOW)</c> syscall gives
    /// on supported architectures, which is exactly why that path is preferred whenever available.
    /// <para>
    /// Internal rather than private specifically so a test can exercise this exact method directly
    /// (<c>OwnerOnlyDirectoryHelperTests</c>), independent of which platform branch
    /// <see cref="ReassertOrThrow"/> would route to on the CI host's own architecture — the symlink
    /// check above must be verified regardless of whether that host happens to be one
    /// <see cref="GetSafeReassertFlags"/> recognizes.
    /// </para>
    /// </remarks>
    [UnsupportedOSPlatform("windows")]
    internal static bool ReassertModeFollowingSymlinks(string segment, ILogger? logger)
    {
        if (new DirectoryInfo(segment).LinkTarget is not null)
        {
            LogNotASafeDirectory(logger, segment);
            return false;
        }

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
            LogSegmentGone(logger, segment, ex);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            // The non-cooperating writer this fix defends against (see the class remarks) won the
            // race running as a DIFFERENT OS user, so this process cannot chmod a directory it does
            // not own — and must not build further segments under a directory it does not control.
            LogAccessDenied(logger, segment, ex);
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

}
