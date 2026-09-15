using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Infrastructure.AI.Helpers;

/// <summary>
/// Linux-architecture-specific half of <see cref="OwnerOnlyDirectoryHelper"/> (altitude/simplify
/// finding, #670/#677 follow-up): the safe, TOCTOU-proof <c>open(O_NOFOLLOW | O_DIRECTORY)</c> +
/// <c>fchmod</c> reassert path, and the per-architecture flag-value mapping it depends on, are pure
/// platform trivia — kernel-header archaeology, syscall flag values, errno mapping — that a reader of
/// <see cref="Create"/> never needs to see. Split into its own partial specifically because this
/// concern has already grown once (#677 added the architecture-mapping machinery) and pushed the main
/// file well past this repo's own file-size guideline; the split follows the same
/// <c>PlanExecutor.Scheduling.cs</c>-style naming this codebase already uses elsewhere for exactly
/// this kind of thematically-separable growth.
/// </summary>
internal static partial class OwnerOnlyDirectoryHelper
{
    /// <summary>
    /// The <c>O_NOFOLLOW</c>/<c>O_DIRECTORY</c> numeric values for one architecture family, as read
    /// from that family's own Linux kernel uapi header (see <see cref="GetSafeReassertFlags"/>).
    /// </summary>
    internal readonly record struct SafeReassertFlags(int NoFollow, int Directory);

    /// <summary>
    /// Maps a process architecture to the <c>O_NOFOLLOW</c>/<c>O_DIRECTORY</c> numeric values that
    /// architecture's own Linux kernel headers define, or <see langword="null"/> if this helper has
    /// not verified them for that architecture (#677). A separate, pure, architecture-independent
    /// method rather than an inline switch in <see cref="Create"/> specifically so it can be unit
    /// tested for every architecture directly, without needing a matching physical host.
    /// </summary>
    /// <remarks>
    /// Every value below was read directly from <c>torvalds/linux</c>'s own uapi headers, not glibc
    /// docs or memory — the exact category of mistake #677 was filed to prevent a repeat of:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="Architecture.X64"/> and <see cref="Architecture.S390x"/>: neither
    /// <c>arch/x86/include/uapi/asm/fcntl.h</c> nor <c>arch/s390/include/uapi/asm/fcntl.h</c> exists
    /// in the kernel source tree, so both fall through to
    /// <c>include/uapi/asm-generic/fcntl.h</c>'s <c>O_DIRECTORY=(1&lt;&lt;16)=0x10000</c> and
    /// <c>O_NOFOLLOW=(1&lt;&lt;17)=0x20000</c>.
    /// </description></item>
    /// <item><description>
    /// <see cref="Architecture.Arm64"/> and <see cref="Architecture.Ppc64le"/>:
    /// <c>arch/arm64/include/uapi/asm/fcntl.h</c> and <c>arch/powerpc/include/uapi/asm/fcntl.h</c>
    /// each <c>#define</c> their own <c>O_DIRECTORY=(1&lt;&lt;14)=0x4000</c> and
    /// <c>O_NOFOLLOW=(1&lt;&lt;15)=0x8000</c> — the exact bits x86_64/s390x use for
    /// <c>O_DIRECT</c>/<c>O_LARGEFILE</c> — before including the generic header, whose include
    /// guards then skip redefining them.
    /// </description></item>
    /// </list>
    /// Any other architecture (32-bit ARM/PowerPC, RISC-V, LoongArch64, WASM, ...) is not covered:
    /// this template has no kernel-source citation for it, so it is left on the plain,
    /// symlink-following fallback rather than guessed at.
    /// </remarks>
    internal static SafeReassertFlags? GetSafeReassertFlags(Architecture architecture) => architecture switch
    {
        Architecture.X64 or Architecture.S390x => new SafeReassertFlags(NoFollow: 0x20000, Directory: 0x10000),
        Architecture.Arm64 or Architecture.Ppc64le => new SafeReassertFlags(NoFollow: 0x8000, Directory: 0x4000),
        _ => null,
    };

    /// <summary>
    /// Re-asserts owner-only mode via <see cref="ApplyOwnerOnlyModeSafely"/>, which never follows a
    /// symlink planted at <paramref name="segment"/> between the create call and this one. Returns
    /// whether <paramref name="segment"/> is confirmed owner-only and safe to build further segments
    /// under; never throws.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static bool ReassertModeOnLinux(string segment, int oNoFollow, int oDirectory, ILogger? logger)
    {
        var (outcome, errno) = ApplyOwnerOnlyModeSafely(segment, oNoFollow, oDirectory);
        switch (outcome)
        {
            case ChmodOutcome.Applied:
                return true;

            case ChmodOutcome.SegmentGone:
                // If Create() continued past this, its own next CreateDirectory call would silently
                // re-create this segment with the BCL's loose default mode — the exact bug this whole
                // fix exists to close, just via a benign race instead of an adversarial one.
                LogSegmentGone(logger, segment);
                return false;

            case ChmodOutcome.NotASafeDirectory:
                // ELOOP or ENOTDIR: the segment stopped being a plain directory between the create
                // call and this one — most plausibly a symlink swap. Refusing to follow it, and
                // refusing to build further segments under it, is the entire point.
                LogNotASafeDirectory(logger, segment);
                return false;

            case ChmodOutcome.AccessDenied:
                LogAccessDenied(logger, segment);
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
    /// Only ever called for an architecture <see cref="GetSafeReassertFlags"/> recognizes (gated in
    /// <see cref="Create"/>) — <paramref name="oNoFollow"/>/<paramref name="oDirectory"/> must be that
    /// architecture's own verified values. See the class remarks for why an unrecognized architecture
    /// is never guessed at instead of routed here.
    /// </remarks>
    [SupportedOSPlatform("linux")]
    private static (ChmodOutcome Outcome, int Errno) ApplyOwnerOnlyModeSafely(string segment, int oNoFollow, int oDirectory)
    {
        const int oRdOnly = 0;
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
