using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Tests.Resilience;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// Tests for <see cref="OwnerOnlyDirectoryHelper"/>.
/// </summary>
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

    /// <summary>
    /// The mode <see cref="CreateAttackerOwnedTarget"/> sets — deliberately distinguishable from
    /// owner-only, so a test can assert the target was left untouched rather than coincidentally
    /// ending up owner-only some other way.
    /// </summary>
    private const UnixFileMode AttackerOwnedTargetMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// Creates a fresh directory an "attacker" fully controls, for the three tests below that plant a
    /// symlink at <see cref="_root"/> pointing at it (/simplify finding: this setup, and its matching
    /// teardown, was duplicated near-verbatim across all three before being factored out here).
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static string CreateAttackerOwnedTarget()
    {
        var target = Path.Combine(Path.GetTempPath(), "owner-only-dir-tests-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, AttackerOwnedTargetMode);
        return target;
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
    public void Create_DirectoryAlreadyExists_DoesNotThrow()
    {
        Directory.CreateDirectory(_root);

        var act = () => OwnerOnlyDirectoryHelper.Create(_root);

        act.Should().NotThrow();
    }

    [Fact]
    public void Create_LeafAlreadyExistsWithLoosePermissions_RetroactivelySecuresIt()
    {
        // #670: a host upgraded in place may have created this exact storage root before this helper
        // existed, with the BCL's loose default mode. Create() must correct it on the next call for
        // that root, not just leave it at whatever mode it already had.
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_root);
        const UnixFileMode wideMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(_root, wideMode);

        OwnerOnlyDirectoryHelper.Create(_root);

        const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.GetUnixFileMode(_root).Should().Be(expected,
            "the exact directory this call was asked to secure must be corrected even if it already existed");
    }

    [Fact]
    public void Create_LeafAlreadyExistsAsASymlink_ThrowsAndDoesNotChmodTheTarget()
    {
        // #670's retroactive path must refuse a pre-existing leaf that is itself a symlink on EVERY
        // POSIX platform, not just architectures GetSafeReassertFlags recognizes (security-review
        // finding): before that finding's fix, a symlink planted at leisure — no race required, unlike
        // the #648 TOCTOU this helper otherwise defends against — would be silently followed by the
        // plain File.SetUnixFileMode fallback on macOS/BSD/unrecognized Linux architectures. Gating on
        // Windows only, not architecture, means this test exercises the fallback's own symlink check
        // wherever this suite happens to run, not only the open(O_NOFOLLOW) safe path.
        if (OperatingSystem.IsWindows())
            return;

        var attackerOwnedTarget = CreateAttackerOwnedTarget();

        try
        {
            Directory.CreateSymbolicLink(_root, attackerOwnedTarget);

            var act = () => OwnerOnlyDirectoryHelper.Create(_root);

            act.Should().Throw<IOException>().WithMessage($"*{_root}*");
            File.GetUnixFileMode(attackerOwnedTarget).Should().Be(AttackerOwnedTargetMode,
                "the symlink target must never be chmod'd through the swapped leaf");
        }
        finally
        {
            // /code-review finding: deleting attackerOwnedTarget alone leaves _root as a DANGLING
            // symlink — Dispose()'s Directory.Exists(_root) guard follows the (now-broken) link, gets
            // false, and skips cleanup, orphaning the symlink in the shared OS temp directory on every
            // run. Deleting the symlink itself first (Directory.Delete on a reparse point removes only
            // the link, never recursing into its target — the same behavior the intermediate-segment
            // symlink test below already relies on) leaves nothing for Dispose() to miss.
            Directory.Delete(_root);
            Directory.Delete(attackerOwnedTarget, recursive: true);
        }
    }

    [Fact]
    public void ReassertModeFollowingSymlinks_SegmentIsASymlink_RefusesAndDoesNotChmodTheTarget()
    {
        // Direct, architecture-independent coverage of the security-review fix: calls the fallback
        // method itself, bypassing ReassertOrThrow's platform branching entirely, so this proves the
        // fallback's own symlink check works regardless of what CPU architecture this test happens to
        // run on — Create_LeafAlreadyExistsAsASymlink_ThrowsAndDoesNotChmodTheTarget above only
        // exercises this method when the host architecture routes there in the first place.
        if (OperatingSystem.IsWindows())
            return;

        var attackerOwnedTarget = CreateAttackerOwnedTarget();

        try
        {
            Directory.CreateSymbolicLink(_root, attackerOwnedTarget);

            var secured = OwnerOnlyDirectoryHelper.ReassertModeFollowingSymlinks(_root, logger: null);

            secured.Should().BeFalse("a symlink must be refused, never followed and chmod'd");
            File.GetUnixFileMode(attackerOwnedTarget).Should().Be(AttackerOwnedTargetMode,
                "the symlink target must never be chmod'd through the swapped leaf");
        }
        finally
        {
            // See the identical comment on Create_LeafAlreadyExistsAsASymlink_ThrowsAndDoesNotChmodTheTarget.
            Directory.Delete(_root);
            Directory.Delete(attackerOwnedTarget, recursive: true);
        }
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
        OwnerOnlyDirectoryHelper.RaceSimulationHookForTests.Value = segment => Directory.CreateDirectory(segment);
        try
        {
            OwnerOnlyDirectoryHelper.Create(leaf);
        }
        finally
        {
            OwnerOnlyDirectoryHelper.RaceSimulationHookForTests.Value = null;
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
        if (!OperatingSystem.IsLinux() || OwnerOnlyDirectoryHelper.GetSafeReassertFlags(RuntimeInformation.ProcessArchitecture) is null)
            return; // the symlink-safe reassert (open(O_NOFOLLOW) + fchmod) is gated to architectures
                    // GetSafeReassertFlags recognizes — see the class remarks on why others use the
                    // plain fallback, and OwnerOnlyDirectoryHelperArchitectureFlagsTests for coverage
                    // of the flag values themselves independent of the CI host's own architecture.

        // #648 round 3 (/code-review): the first two attempts at this fix logged a failed reassert
        // and kept building deeper segments anyway. Ordinary path resolution follows a symlink at ANY
        // component of a multi-segment path, not just the one open(O_NOFOLLOW) refuses to follow — so
        // swapping an INTERMEDIATE segment (not the leaf) proves the property this fix actually needs:
        // nothing gets created under a parent this call could not confirm as owner-only.
        var attackerOwnedTarget = CreateAttackerOwnedTarget();

        try
        {
            var compromisedSegment = Path.Combine(_root, "a");
            var leaf = Path.Combine(compromisedSegment, "b", "c");
            var logger = new RecordingLogger<OwnerOnlyDirectoryHelperTests>();
            OwnerOnlyDirectoryHelper.RaceSimulationHookForTests.Value = segment =>
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
                OwnerOnlyDirectoryHelper.RaceSimulationHookForTests.Value = null;
            }

            File.GetUnixFileMode(attackerOwnedTarget).Should().Be(AttackerOwnedTargetMode,
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
