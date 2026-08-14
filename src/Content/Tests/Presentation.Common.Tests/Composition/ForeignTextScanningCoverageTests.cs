using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Two channels carry externally-authored text into the model's context the same way an MCP tool
/// description does — a plugin skill's manifest and an agent's manifest — and #331 wired both through
/// <c>IMcpSecurityScanner</c>. This asserts the wiring is real (the parsers reference the scanner) and
/// that it cannot be bypassed (nothing outside the two parsers constructs the definition types the
/// scan gates), the same "is the control actually called, and is it the only path" pairing
/// <c>SecurityControlHasACallerTests</c> and <c>ToolCallAdmissionChokepointTests</c> already apply to
/// governance and tool-admission controls.
/// </summary>
public sealed class ForeignTextScanningCoverageTests
{
    [Fact]
    public void SkillMetadataParser_ReferencesTheScanner()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var file = FindFile(contentRoot, "SkillMetadataParser.cs");

        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(file));

        Regex.IsMatch(code, @"\bIMcpSecurityScanner\b").Should().BeTrue(
            "SkillMetadataParser must screen a plugin-sourced skill's manifest through the security "
            + "scanner before constructing a SkillDefinition — a plugin is third-party content by "
            + "definition (#331)");
    }

    [Fact]
    public void AgentMetadataParser_ReferencesTheScanner()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var file = FindFile(contentRoot, "AgentMetadataParser.cs");

        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(file));

        Regex.IsMatch(code, @"\bIMcpSecurityScanner\b").Should().BeTrue(
            "AgentMetadataParser must screen an AGENT.md manifest through the security scanner before "
            + "constructing an AgentDefinition (#331)");
    }

    [Fact]
    public void OnlyTheParsersConstructTheirDefinitionTypes()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(contentRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (SourceScan.IsExcluded(file, contentRoot))
                continue;

            var fileName = Path.GetFileName(file);
            var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(file));

            if (!string.Equals(fileName, "SkillMetadataParser.cs", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(code, @"\bnew\s+SkillDefinition\b"))
            {
                offenders.Add($"{Path.GetRelativePath(contentRoot, file)} constructs SkillDefinition");
            }

            if (!string.Equals(fileName, "AgentMetadataParser.cs", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(code, @"\bnew\s+AgentDefinition\b"))
            {
                offenders.Add($"{Path.GetRelativePath(contentRoot, file)} constructs AgentDefinition");
            }
        }

        offenders.Should().BeEmpty(
            "SkillMetadataParser.Build and AgentMetadataParser.ParseFromFile are the only places these "
            + "types may be constructed — that's where the #331 injection scan runs. A second "
            + "construction site would bypass the scan entirely, the mirror image of a security control "
            + "with no caller. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheGuardWouldActuallyFire()
    {
        var violating = SourceScan.StripCommentsAndStrings(
            "var s = new SkillDefinition { Id = \"x\" };");
        var commentOnly = SourceScan.StripCommentsAndStrings(
            "// see SkillDefinition for the shape it must have\npublic class X { }");

        Regex.IsMatch(violating, @"\bnew\s+SkillDefinition\b").Should().BeTrue();
        Regex.IsMatch(commentOnly, @"\bnew\s+SkillDefinition\b").Should().BeFalse(
            "a doc comment naming the type must not count as constructing it");
    }

    [Fact]
    public void TheScanReadsARepresentativeNumberOfFiles()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        Directory.EnumerateFiles(contentRoot, "*.cs", SearchOption.AllDirectories)
            .Count(f => !SourceScan.IsExcluded(f, contentRoot))
            .Should().BeGreaterThan(500);
    }

    private static string FindFile(string contentRoot, string fileName)
    {
        var matches = Directory.EnumerateFiles(contentRoot, fileName, SearchOption.AllDirectories)
            .Where(f => !SourceScan.IsExcluded(f, contentRoot))
            .ToArray();

        matches.Should().ContainSingle(
            $"exactly one production {fileName} must exist for this test's verdict to mean anything");

        return matches[0];
    }
}
