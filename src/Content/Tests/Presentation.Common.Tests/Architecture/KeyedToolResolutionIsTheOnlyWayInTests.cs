using System.Text.RegularExpressions;
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
    /// <summary>
    /// The spellings that build a keyed tool: the generic resolution methods (any whitespace, namespace-qualified or
    /// not), the non-generic overloads taking <c>typeof(ITool)</c>, and constructor injection with
    /// <c>[FromKeyedServices]</c>.
    /// </summary>
    private static readonly Regex[] Forbidden =
    [
        new(@"KeyedServices?\s*<\s*([\w.]+\.)?ITool\s*>", RegexOptions.Compiled),
        new(@"KeyedServices?\s*\(\s*typeof\s*\(\s*([\w.]+\.)?ITool\s*\)", RegexOptions.Compiled),
        new(@"FromKeyedServices\s*\([^)]*\)\s*\]\s*([\w.]+\.)?ITool\b", RegexOptions.Compiled),
    ];

    /// <summary>The one file allowed to resolve a keyed tool directly.</summary>
    private const string TheResolution = "Application/Application.AI.Common/Services/Tools/KeyedToolResolution.cs";

    [Theory]
    [InlineData("sp.GetKeyedService<ITool>(key)")]
    [InlineData("sp.GetRequiredKeyedService< Application.AI.Common.Interfaces.Tools.ITool >(key)")]
    [InlineData("sp.GetKeyedServices<ITool>(key)")]
    [InlineData("sp.GetRequiredKeyedService(typeof(ITool), key)")]
    [InlineData("public Foo([FromKeyedServices(\"bash\")] ITool bash)")]
    public void TheGuard_RecognisesEverySpellingThatBuildsAKeyedTool(string code) =>
        Forbidden.Should().Contain(r => r.IsMatch(code), "an unrecognised spelling is a way past this guard");

    [Fact]
    public void AKeyedTool_IsResolvedOnlyThroughKeyedToolResolution()
    {
        var violations = ReadProductionSources()
            .Where(s => Forbidden.Any(f => f.IsMatch(s.Code)))
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
    /// The exemption must point at a real file: if the resolution moved, the exemption would silently cover nothing
    /// and the next file to resolve a tool would be flagged for the wrong reason. That the file still checks the name
    /// is proven by behaviour, in <c>KeyedToolResolutionTests</c>, not by reading its text.
    /// </summary>
    [Fact]
    public void TheExemptFile_StillExists()
    {
        ReadProductionSources()
            .Count(s => s.Path.Replace(Path.DirectorySeparatorChar, '/').EndsWith("/" + TheResolution, StringComparison.Ordinal))
            .Should().Be(1, $"{TheResolution} is where keyed tools are built; if it moved, update this guard");
    }

    private static IReadOnlyList<(string Path, string Code)> ReadProductionSources()
    {
        var sources = SourceScan.ReadProductionSources(Path.Combine(RepoRoot.Path, "src", "Content"));

        // A vacuous architecture test reads as protection; the floor is near the tree's real size.
        sources.Should().HaveCountGreaterThan(2000, "the scan must see the production tree, not a sliver of it");
        return sources;
    }
}
