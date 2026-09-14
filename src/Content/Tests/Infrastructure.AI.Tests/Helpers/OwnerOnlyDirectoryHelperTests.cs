using System.Runtime.InteropServices;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Tests.Resilience;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// Tests for <see cref="OwnerOnlyDirectoryHelper"/>.
/// </summary>
/// <remarks>
/// In <see cref="OwnerOnlyDirectoryRaceHookCollection"/> (#676): several tests here set the shared
/// static <see cref="OwnerOnlyDirectoryHelper.RaceSimulationHookForTests"/> seam, which xUnit's default
/// cross-class parallelization could otherwise let another test class transiently observe.
/// </remarks>
[Collection(OwnerOnlyDirectoryRaceHookCollection.Name)]
public sealed class OwnerOnlyDirectoryHelperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "owner-only-dir-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup
        }
    }

    [Fact]
    public void Create_MultiLevelPathWithNoExistingSegments_AppliesOwnerOnlyModeToEveryCreatedLevel()
    {
        // /code-review finding: Directory.CreateDirectory(path, mode)'s single-call overload only
        // applies the mode to the FINAL path segment — every missing intermediate directory it
        // creates gets the loose BCL default instead. This asserts the fix closes that gap for every
        // level this call creates, not just the leaf.
        var leaf = Path.Combine(_root, "a", "b", "c");

        OwnerOnlyDirectoryHelper.Create(leaf);

        if (OperatingSystem.IsWindows())
        {
            Directory.Exists(leaf).Should().BeTrue();
            return;
        }

        const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.GetUnixFileMode(_root).Should().Be(expected, "the root segment this call newly created must be owner-only too");
        File.GetUnixFileMode(Path.Combine(_root, "a")).Should().Be(expected);
        File.GetUnixFileMode(Path.Combine(_root, "a", "b")).Should().Be(expected);
        File.GetUnixFileMode(leaf).Should().Be(expected);
    }

    [Fact]
    public void Create_DirectoryAlreadyExists_IsANoOpAndDoesNotThrow()
    {
        Directory.CreateDirectory(_root);

        var act = () => OwnerOnlyDirectoryHelper.Create(_root);

        act.Should().NotThrow();
    }

    [Fact]
    public void Create_NonCooperatingWriterWinsTheRaceWithLooseDefaultMode_ModeIsStillCorrectedToOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        // #648: the actual race this fix closes is a writer that does NOT go through this helper —
        // e.g. a plain Directory.CreateDirectory(path) call elsewhere — winning the race to create
        // THIS call's segment first, with the BCL's loose default mode, before this call's own
        // CreateDirectory(segment, OwnerOnlyMode) reaches it (a permission no-op on an already-
        // existing directory). A COOPERATING racer that also calls Create() can never trigger this:
        // every Create() caller requests the identical owner-only mode, and CreateDirectory applies
        // the WINNING caller's requested mode atomically at creation — verified empirically via a
        // real concurrent-racer run against the pre-fix code (0 mode mismatches across 2,560 racing
        // creations). The hook below simulates the one writer shape that actually can lose the mode,
        // deterministically, instead of relying on real thread scheduling to land in a timing window
        // real concurrency can't reliably force.
        var leaf = Path.Combine(_root, "a", "b", "c");
        OwnerOnlyDirectoryHelper.RaceSimulationHookForTests = segment => Directory.CreateDirectory(segment);
        try
        {
            OwnerOnlyDirectoryHelper.Create(leaf);
        }
        finally
        {
            OwnerOnlyDirectoryHelper.RaceSimulationHookForTests = null;
        }

        const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.GetUnixFileMode(_root).Should().Be(expected);
        File.GetUnixFileMode(Path.Combine(_root, "a")).Should().Be(expected);
        File.GetUnixFileMode(Path.Combine(_root, "a", "b")).Should().Be(expected);
        File.GetUnixFileMode(leaf).Should().Be(expected);
    }

    [Fact]
    public void Create_IntermediateSegmentReplacedWithSymlink_AbortsBeforeBuildingUnderIt()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return; // the symlink-safe reassert (open(O_NOFOLLOW) + fchmod) is gated to Linux/x86_64
                    // only — see the class remarks on why other architectures use the plain fallback.

        // #648 round 3 (/code-review): the first two attempts at this fix logged a failed reassert
        // and kept building deeper segments anyway. Ordinary path resolution follows a symlink at ANY
        // component of a multi-segment path, not just the one open(O_NOFOLLOW) refuses to follow — so
        // swapping an INTERMEDIATE segment (not the leaf) proves the property this fix actually needs:
        // nothing gets created under a parent this call could not confirm as owner-only.
        var attackerOwnedTarget = Path.Combine(
            Path.GetTempPath(), "owner-only-dir-tests-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attackerOwnedTarget);
        const UnixFileMode wideMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(attackerOwnedTarget, wideMode);

        try
        {
            var compromisedSegment = Path.Combine(_root, "a");
            var leaf = Path.Combine(compromisedSegment, "b", "c");
            var logger = new RecordingLogger<OwnerOnlyDirectoryHelperTests>();
            OwnerOnlyDirectoryHelper.RaceSimulationHookForTests = segment =>
            {
                if (segment == compromisedSegment)
                    Directory.CreateSymbolicLink(segment, attackerOwnedTarget);
            };

            Action act = () => OwnerOnlyDirectoryHelper.Create(leaf, logger);
            try
            {
                act.Should().Throw<IOException>().WithMessage($"*{compromisedSegment}*");
            }
            finally
            {
                OwnerOnlyDirectoryHelper.RaceSimulationHookForTests = null;
            }

            File.GetUnixFileMode(attackerOwnedTarget).Should().Be(wideMode,
                "the symlink target must never be chmod'd through the swapped segment");
            Directory.Exists(Path.Combine(attackerOwnedTarget, "b")).Should().BeFalse(
                "nothing may be created under a segment this call could not confirm as owner-only");
            logger.Entries.Should().Contain(e =>
                e.Level == LogLevel.Warning && e.Message.Contains(compromisedSegment, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(attackerOwnedTarget, recursive: true);
        }
    }

    [Fact]
    public void Create_SomeParentSegmentsAlreadyExist_OnlyAppliesModeToTheNewlyCreatedOnes()
    {
        var existingParent = Path.Combine(_root, "existing");
        Directory.CreateDirectory(existingParent);
        // A deliberately WIDE mode, distinguishable from owner-only, and set explicitly rather than
        // relying on whatever the ambient umask happens to produce — the assertion below must prove
        // this segment was left untouched, not coincide with owner-only by chance.
        const UnixFileMode wideMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(existingParent, wideMode);
        var leaf = Path.Combine(existingParent, "new-a", "new-b");

        var act = () => OwnerOnlyDirectoryHelper.Create(leaf);

        act.Should().NotThrow();
        Directory.Exists(leaf).Should().BeTrue();

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.GetUnixFileMode(existingParent).Should().Be(wideMode,
                "a pre-existing segment's permissions must never be touched by a call that only needs to create its children");
            File.GetUnixFileMode(Path.Combine(existingParent, "new-a")).Should().Be(expected);
            File.GetUnixFileMode(leaf).Should().Be(expected);
        }
    }
}
