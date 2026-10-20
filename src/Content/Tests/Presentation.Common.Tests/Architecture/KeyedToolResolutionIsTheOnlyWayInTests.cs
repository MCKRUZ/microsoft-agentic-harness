using Application.AI.Common.Exceptions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Architecture guard (#888): a first-party tool is built from its keyed registration only through
/// <c>KeyedToolResolution.Resolve</c>, which refuses a tool whose reported name differs from its key. Source-scans
/// the production tree and fails when anything else resolves a keyed <c>ITool</c>.
/// </summary>
/// <remarks>
/// Grants, deny rules and manifests name a tool by one string, and the permission code no longer translates a key
/// into a published name, because the two are required to be the same. That requirement holds only while every
/// way a tool is built checks it: a single resolution that skipped the check could hand an agent a tool whose
/// grant was written for something else, and nothing would report it.
/// </remarks>
public sealed class KeyedToolResolutionIsTheOnlyWayInTests
{
    /// <summary>The spellings that build a keyed tool: the generic resolution methods, typed as <c>ITool</c>.</summary>
    private static readonly string[] Forbidden = ["KeyedService<ITool>", "KeyedServices<ITool>"];

    /// <summary>The one file allowed to resolve a keyed tool directly.</summary>
    private const string TheResolution = "Application/Application.AI.Common/Services/Tools/KeyedToolResolution.cs";

    [Fact]
    public void AKeyedTool_IsResolvedOnlyThroughKeyedToolResolution()
    {
        var violations = ReadProductionSources()
            .Where(s => Forbidden.Any(f => s.Code.Contains(f, StringComparison.Ordinal)))
            .Select(s => s.Path.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !path.EndsWith("/" + TheResolution, StringComparison.Ordinal))
            .ToList();

        if (violations.Count > 0)
        {
            Assert.Fail(
                "A keyed ITool was resolved outside KeyedToolResolution:" + Environment.NewLine +
                string.Join(Environment.NewLine, violations.Select(v => "  " + v)) +
                Environment.NewLine + Environment.NewLine +
                "Call KeyedToolResolution.Resolve instead. It refuses a tool whose name differs from its " +
                "registration key (ToolNameMismatchException), which is what lets every grant and deny rule " +
                "name a tool by one string (#888). Treat the exception as the tool being unavailable.");
        }
    }

    /// <summary>
    /// The other direction: the one allowed file must still check the name. Without this, deleting the check leaves
    /// the guard above green while nothing enforces what it exists for.
    /// </summary>
    [Fact]
    public void KeyedToolResolution_StillRefusesAMismatchedName()
    {
        var resolution = ReadProductionSources()
            .Where(s => s.Path.Replace(Path.DirectorySeparatorChar, '/').EndsWith("/" + TheResolution, StringComparison.Ordinal))
            .ToList();

        resolution.Should().ContainSingle($"{TheResolution} is where keyed tools are built; if it moved, update this guard");
        resolution[0].Code.Should().Contain(nameof(ToolNameMismatchException));
    }

    private static IReadOnlyList<(string Path, string Code)> ReadProductionSources()
    {
        var sources = SourceScan.ReadProductionSources(Path.Combine(RepoRoot.Path, "src", "Content"));

        // A vacuous architecture test reads as protection; the floor is near the tree's real size.
        sources.Should().HaveCountGreaterThan(2000, "the scan must see the production tree, not a sliver of it");
        return sources;
    }
}
