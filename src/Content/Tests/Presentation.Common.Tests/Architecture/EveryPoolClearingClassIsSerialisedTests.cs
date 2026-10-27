using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Every test class in this assembly that clears SQLite's connection pools runs in
/// <see cref="SqliteConnectionPoolCollection"/> (#924).
/// </summary>
/// <remarks>
/// A pool-clearing class left outside the collection runs in parallel with the end-to-end tests and can close the
/// connections they hold open, which fails them intermittently, never when run alone. The guard reads the source: it
/// finds direct calls in this project's files, which is every caller today. A class that clears the pools through a
/// helper in another project would not be seen.
/// </remarks>
public sealed class EveryPoolClearingClassIsSerialisedTests
{
    // Built in two parts so this file does not match its own scan.
    private static readonly string ClearCall = "SqliteConnection." + "ClearAllPools();";
    private static readonly string InCollection = "[Collection(" + nameof(SqliteConnectionPoolCollection) + ".Name)]";

    [Fact]
    public void EveryFileThatClearsThePools_IsInTheSerialisedCollection()
    {
        var project = Path.Combine(RepoRoot.Path, "src", "Content", "Tests", "Presentation.Common.Tests");
        var clearing = Directory
            .EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .Select(f => (Path: f, Text: File.ReadAllText(f)))
            .Where(f => f.Text.Contains(ClearCall, StringComparison.Ordinal))
            .ToList();

        clearing.Should().NotBeEmpty("the scan must find the known callers, or it proves nothing");
        clearing.Where(f => !f.Text.Contains(InCollection, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(project, f.Path))
            .Should().BeEmpty("a class that clears every pool must not run beside the tests that hold connections open");
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
