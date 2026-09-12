using FluentAssertions;

namespace Infrastructure.AI.Tests;

/// <summary>
/// Shared assertion for the owner-only (#640, following #527's precedent) directory-permission tests
/// scattered across this assembly — one per migrated storage root.
/// </summary>
internal static class OwnerOnlyDirectoryAssertions
{
    /// <summary>
    /// Asserts <paramref name="directory"/> was created with owner-only read/write/execute
    /// permissions. A no-op on Windows, where <see cref="Infrastructure.AI.Helpers.OwnerOnlyDirectoryHelper"/>
    /// itself is a no-op and there is nothing to assert.
    /// </summary>
    internal static void ShouldBeOwnerOnlyDirectory(this string directory)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.GetUnixFileMode(directory).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
