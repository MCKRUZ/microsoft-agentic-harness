using FluentAssertions;
using Infrastructure.AI.Helpers;
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
    public void Create_SomeParentSegmentsAlreadyExist_OnlyAppliesModeToTheNewlyCreatedOnes()
    {
        var existingParent = Path.Combine(_root, "existing");
        Directory.CreateDirectory(existingParent); // created with the default (non-restricted) mode
        var leaf = Path.Combine(existingParent, "new-a", "new-b");

        var act = () => OwnerOnlyDirectoryHelper.Create(leaf);

        act.Should().NotThrow();
        Directory.Exists(leaf).Should().BeTrue();

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.GetUnixFileMode(Path.Combine(existingParent, "new-a")).Should().Be(expected);
            File.GetUnixFileMode(leaf).Should().Be(expected);
        }
    }
}
