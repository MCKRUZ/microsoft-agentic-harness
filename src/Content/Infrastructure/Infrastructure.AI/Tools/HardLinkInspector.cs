using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Asks the operating system how many directory entries name the file at a given path, so a
/// sandbox can refuse a file it cannot prove has only one name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every other control in <see cref="FileSystemService"/> reasons about a
/// path. A hard link defeats all of them, because it is not an alias of a path — it is a second,
/// equally real directory entry for one file. It carries no reparse point, so
/// <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> returns nothing and its canonical form is
/// itself; a hard link planted inside the workspace therefore presents to every path comparison as
/// an ordinary, unprotected workspace file that happens to read and write protected harness state.
/// Creating one needs no privilege on Windows (<c>mklink /H</c>) or Linux (<c>ln</c>). The only way
/// to see it is to stop asking about the path and ask about the file's identity, which is what this
/// type does.
/// </para>
/// <para>
/// <b>Why the link count and not an identity comparison.</b> Comparing the file's device/inode pair
/// against every file currently inside a protected directory would be more precise, but it costs an
/// enumeration of the protected directory per operation and races against files created in it
/// between the enumeration and the open. The link count needs neither: a file with one directory
/// entry cannot be an alias of anything, whatever else exists on the volume. Legitimate files in an
/// agent workspace are written once by the agent and have exactly one entry, so the imprecision
/// costs nothing in practice — and when it does bite, it bites closed.
/// </para>
/// <para>
/// <b>Why interop.</b> No managed BCL API exposes a link count. Two platform calls are wrapped
/// here, both taking a handle the caller has already opened rather than a path, and both kept
/// deliberately small: <c>GetFileInformationByHandle</c> on Windows and <c>statx</c> on Linux.
/// </para>
/// <para>
/// <b>Platform coverage, and what happens beyond it.</b> Windows and Linux are implemented. Every
/// other platform — macOS and the BSDs — reports <see cref="LinkCount.Unknown"/>, and callers are
/// contractually required to treat that as a denial. Those platforms expose the count only through
/// <c>struct stat</c>, whose field order and widths differ by operating system <em>and</em> by
/// processor architecture; shipping a guessed layout into a template would read the wrong four
/// bytes and fail open silently, which is strictly worse than an honest closed door. A consumer who
/// needs macOS support should implement one additional branch here rather than weaken the contract.
/// </para>
/// </remarks>
internal static class HardLinkInspector
{
    /// <summary>
    /// How many directory entries the operating system reports for a file.
    /// </summary>
    internal enum LinkCount
    {
        /// <summary>
        /// Exactly one directory entry names the file, or there is nothing to inspect (the entry is
        /// a directory, or does not exist yet). The file cannot be an alias of another.
        /// </summary>
        Single,

        /// <summary>
        /// More than one directory entry names the file. It is one end of a hard link, and there is
        /// no way to tell from here which end.
        /// </summary>
        Multiple,

        /// <summary>
        /// The count could not be established: an unimplemented platform, a failed platform call,
        /// or a reported count the caller has no reason to trust. Callers must fail closed on this.
        /// </summary>
        Unknown,
    }

    /// <summary>
    /// Reports how many directory entries name the file at <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Directories short-circuit to <see cref="LinkCount.Single"/> without opening a handle. Neither
    /// platform lets an unprivileged process hard-link a directory, and on Linux every directory
    /// legitimately carries at least two entries (its own <c>.</c> and its parent's), so counting
    /// links there would deny every directory operation for no security gain.
    /// </remarks>
    /// <param name="path">An absolute path. Need not exist.</param>
    /// <returns>The link-count classification; never throws.</returns>
    public static LinkCount Inspect(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                return LinkCount.Single;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing on disk: a file about to be created has no second name. This is the ordinary
            // write-to-a-new-file case, not an error.
            return LinkCount.Single;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException)
        {
            return LinkCount.Unknown;
        }

        return InspectFile(path);
    }

    /// <summary>
    /// Opens a handle to an existing file and dispatches to the platform call.
    /// </summary>
    /// <param name="path">An absolute path to an entry known not to be a directory.</param>
    private static LinkCount InspectFile(string path)
    {
        try
        {
            // FileShare.ReadWrite | Delete so inspecting never contends with a legitimate writer,
            // and FileAccess.Read because that is the least access that yields a usable handle.
            using var handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (OperatingSystem.IsWindows())
                return WindowsLinkCount(handle);

            return OperatingSystem.IsLinux() ? LinuxLinkCount(handle) : LinkCount.Unknown;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the attribute read and the open.
            return LinkCount.Single;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException
                                      or DllNotFoundException
                                      or EntryPointNotFoundException)
        {
            // A file that cannot be opened cannot be inspected. The caller denies, which costs
            // nothing: a file that cannot be opened cannot be read or written either.
            return LinkCount.Unknown;
        }
    }

    /// <summary>
    /// Turns a raw platform link count into a verdict, distrusting a count of zero.
    /// </summary>
    /// <remarks>
    /// A live file always has at least one directory entry, so zero means the platform call
    /// reported something this code does not understand — a wrong struct offset being the obvious
    /// candidate. Treating it as <see cref="LinkCount.Unknown"/> makes that mistake fail closed
    /// instead of reading as "no links, therefore safe".
    /// </remarks>
    /// <param name="rawCount">The count as reported by the platform.</param>
    private static LinkCount Classify(uint rawCount) => rawCount switch
    {
        0 => LinkCount.Unknown,
        1 => LinkCount.Single,
        _ => LinkCount.Multiple,
    };

    // ---- Windows ----------------------------------------------------------------------------

    /// <summary>
    /// Reads <c>nNumberOfLinks</c> from <c>GetFileInformationByHandle</c>.
    /// </summary>
    /// <param name="handle">An open handle to the file.</param>
    [SupportedOSPlatform("windows")]
    private static LinkCount WindowsLinkCount(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var information)
            ? Classify(information.NumberOfLinks)
            : LinkCount.Unknown;

    /// <summary>
    /// The Win32 <c>BY_HANDLE_FILE_INFORMATION</c> structure.
    /// </summary>
    /// <remarks>
    /// Every field is declared, in order, even though only <see cref="NumberOfLinks"/> is read. The
    /// operating system writes the whole 52-byte structure, so a truncated declaration would be a
    /// buffer overrun, and declaring the fields is safer than hand-computing one offset. The two
    /// <c>FILETIME</c> values are spelled as their low/high halves so the type carries no
    /// dependency beyond the primitives.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle, out ByHandleFileInformation information);

    // ---- Linux ------------------------------------------------------------------------------

    /// <summary>Size of the kernel's <c>struct statx</c>, which callers must supply in full.</summary>
    private const int StatxSize = 256;

    /// <summary><c>STATX_NLINK</c> — request (and confirm receipt of) <c>stx_nlink</c>.</summary>
    private const uint StatxNlink = 0x0000_0004;

    /// <summary><c>AT_EMPTY_PATH</c> — operate on the descriptor, ignoring the path argument.</summary>
    private const int AtEmptyPath = 0x1000;

    /// <summary>
    /// Reads <c>stx_nlink</c> from <c>statx</c>, applied to the already-open descriptor.
    /// </summary>
    /// <remarks>
    /// <c>statx</c> rather than <c>stat</c> deliberately: <c>struct statx</c> is a kernel UAPI
    /// structure with one fixed layout across every architecture, whereas <c>struct stat</c> is
    /// arranged differently on x86-64 and AArch64 and reaches glibc through versioned symbols. The
    /// returned <c>stx_mask</c> is checked rather than assumed, so a kernel or filesystem that did
    /// not fill the field produces a denial instead of a fabricated count.
    /// </remarks>
    /// <param name="handle">An open handle to the file; its descriptor is passed to the syscall.</param>
    [SupportedOSPlatform("linux")]
    private static LinkCount LinuxLinkCount(SafeFileHandle handle)
    {
        var referenced = false;
        try
        {
            handle.DangerousAddRef(ref referenced);
            if (!referenced)
                return LinkCount.Unknown;

            // Empty path + AT_EMPTY_PATH means "the file this descriptor already refers to", so the
            // count describes the handle the caller opened rather than whatever the path names now.
            if (Statx((int)handle.DangerousGetHandle(), string.Empty, AtEmptyPath, StatxNlink, out var status) != 0)
                return LinkCount.Unknown;

            return (status.Mask & StatxNlink) == 0 ? LinkCount.Unknown : Classify(status.LinkCount);
        }
        finally
        {
            if (referenced)
                handle.DangerousRelease();
        }
    }

    /// <summary>
    /// The leading fields of the kernel's <c>struct statx</c>.
    /// </summary>
    /// <remarks>
    /// Explicit offsets, taken from the kernel UAPI: <c>stx_mask</c> (u32) at 0, <c>stx_blksize</c>
    /// (u32) at 4, <c>stx_attributes</c> (u64) at 8, <c>stx_nlink</c> (u32) at 16. The declared size
    /// is the full structure the kernel writes, so the unread remainder is still allocated.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = StatxSize)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int dirFd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname,
        int flags,
        uint mask,
        out StatxResult status);
}
