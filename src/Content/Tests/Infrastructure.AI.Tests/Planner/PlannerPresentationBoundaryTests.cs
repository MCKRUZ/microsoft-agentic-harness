using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Architecture guard for the plan-run arming boundary: no Presentation-layer code may depend on the
/// ungoverned <c>IPlanExecutor</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>IPlanRunExecutor</c> is the one place a plan run's capability envelope and governance identity
/// are armed. <c>IPlanExecutor</c> sits beside it in the same DI container and arms neither — a host
/// surface that injects it executes plans completely outside the envelope, with no runtime signal
/// that the whole confinement layer was skipped. XML docs saying "don't" cannot enforce that, so this
/// test does.
/// </para>
/// <para>
/// Implemented as a source scan rather than reflection because Infrastructure.AI.Tests deliberately
/// does not reference the Presentation assemblies (that dependency would itself invert the layering
/// this test defends). It walks up to the repository root and reads the Presentation tree; if that
/// tree cannot be located the test fails loudly rather than passing vacuously.
/// </para>
/// </remarks>
public sealed class PlannerPresentationBoundaryTests
{
    [Fact]
    public void NoPresentationSourceFile_ReferencesTheUngovernedPlanExecutor()
    {
        var presentationRoot = LocatePresentationRoot();

        var offenders = Directory
            .EnumerateFiles(presentationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedOrBuildOutput(path))
            .Where(path => ReferencesUngovernedExecutor(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(presentationRoot, path))
            .ToList();

        offenders.Should().BeEmpty(
            "Presentation must drive plans through IPlanRunExecutor, which arms the caller's capability "
            + "envelope and governance identity; IPlanExecutor arms neither and silently bypasses "
            + "envelope confinement");
    }

    /// <summary>
    /// Matches <c>IPlanExecutor</c> as a whole identifier so <c>IPlanRunExecutor</c> — the interface
    /// Presentation is *supposed* to use — does not trip the guard on a naive substring match.
    /// </summary>
    private static bool ReferencesUngovernedExecutor(string source)
    {
        var index = source.IndexOf("IPlanExecutor", StringComparison.Ordinal);
        while (index >= 0)
        {
            var precededByIdentifierChar = index > 0
                && (char.IsLetterOrDigit(source[index - 1]) || source[index - 1] == '_');

            var end = index + "IPlanExecutor".Length;
            var followedByIdentifierChar = end < source.Length
                && (char.IsLetterOrDigit(source[end]) || source[end] == '_');

            if (!precededByIdentifierChar && !followedByIdentifierChar)
                return true;

            index = source.IndexOf("IPlanExecutor", index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsGeneratedOrBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string LocatePresentationRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Content", "Presentation");
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/Content/Presentation from the test output directory. This guard must "
            + "not silently pass — fix the path walk rather than deleting the test.");
    }
}
