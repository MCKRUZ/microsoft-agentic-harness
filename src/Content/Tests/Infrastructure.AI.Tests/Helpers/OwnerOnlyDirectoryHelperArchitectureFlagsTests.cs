using System.Runtime.InteropServices;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Xunit;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// Tests <see cref="OwnerOnlyDirectoryHelper.GetSafeReassertFlags"/> — the pure architecture-to-flags
/// mapping factored out specifically so every architecture's values can be asserted directly, without
/// needing a matching physical host to exercise <c>ApplyOwnerOnlyModeSafely</c> itself (#677).
/// </summary>
public sealed class OwnerOnlyDirectoryHelperArchitectureFlagsTests
{
    /// <summary>
    /// x86_64 and s390 define no architecture-specific <c>fcntl.h</c> override in the Linux kernel
    /// source tree, so both fall through to <c>include/uapi/asm-generic/fcntl.h</c>'s
    /// <c>O_DIRECTORY=(1&lt;&lt;16)</c>/<c>O_NOFOLLOW=(1&lt;&lt;17)</c> — verified directly against
    /// <c>torvalds/linux</c>, matching this helper's pre-#677 x86_64-only values exactly.
    /// </summary>
    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.S390x)]
    public void GetSafeReassertFlags_X64OrS390x_ReturnsGenericHeaderValues(Architecture architecture)
    {
        var flags = OwnerOnlyDirectoryHelper.GetSafeReassertFlags(architecture);

        flags.Should().NotBeNull();
        flags!.Value.NoFollow.Should().Be(0x20000);
        flags.Value.Directory.Should().Be(0x10000);
    }

    /// <summary>
    /// ARM64 and PowerPC (ppc64le) each define their OWN <c>fcntl.h</c> override —
    /// <c>O_DIRECTORY=(1&lt;&lt;14)</c>/<c>O_NOFOLLOW=(1&lt;&lt;15)</c> — before including the generic
    /// header. These are the exact bits x86_64/s390x use for <c>O_DIRECT</c>/<c>O_LARGEFILE</c>: using
    /// the x86_64 values here would silently disarm the symlink-safe reassert instead of failing loudly
    /// (the defect #677 was filed to prevent a repeat of).
    /// </summary>
    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.Ppc64le)]
    public void GetSafeReassertFlags_Arm64OrPpc64le_ReturnsArchSpecificOverrideValues(Architecture architecture)
    {
        var flags = OwnerOnlyDirectoryHelper.GetSafeReassertFlags(architecture);

        flags.Should().NotBeNull();
        flags!.Value.NoFollow.Should().Be(0x8000);
        flags.Value.Directory.Should().Be(0x4000);
        flags.Value.NoFollow.Should().NotBe(0x20000, "these bits mean O_LARGEFILE on ARM64/PowerPC, not O_NOFOLLOW");
        flags.Value.Directory.Should().NotBe(0x10000, "these bits mean O_DIRECT on ARM64/PowerPC, not O_DIRECTORY");
    }

    /// <summary>
    /// An architecture this helper has no kernel-source citation for must fall back to the plain,
    /// symlink-following path rather than receive a guessed value — the same reasoning the class
    /// remarks document for why x86_64/s390x and ARM64/PowerPC were verified individually instead of
    /// assumed to share one set of values.
    /// </summary>
    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.LoongArch64)]
    [InlineData(Architecture.Wasm)]
    public void GetSafeReassertFlags_UnverifiedArchitecture_ReturnsNull(Architecture architecture)
    {
        OwnerOnlyDirectoryHelper.GetSafeReassertFlags(architecture).Should().BeNull();
    }
}
