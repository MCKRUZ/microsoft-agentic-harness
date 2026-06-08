using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools.Workspace;

/// <summary>
/// Verifies the actual <c>plugins/workspace-skill/skills/workspace/SKILL.md</c>
/// shipping in the repo parses to a SkillDefinition with the expected
/// allowed-tools list and an empty egress allowlist. This is what guarantees
/// the "deny-all egress" and "5 allow-listed tools" claims in the SKILL.md are
/// authoritative — if the file's frontmatter drifts away from the contract,
/// these tests fail loudly.
/// </summary>
public sealed class WorkspaceSkillManifestTests
{
    [Fact]
    public void ShippedSkillMd_HasExpectedAllowedToolsAndEmptyEgressAllowlist()
    {
        var skillPath = LocateShippedSkillMd();
        File.Exists(skillPath).Should().BeTrue(
            $"the workspace SKILL.md must ship in the repo at {skillPath}");

        var parser = new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance);
        var skill = parser.ParseFromFile(skillPath, Path.GetDirectoryName(skillPath)!, pluginSource: "workspace-skill");

        skill.Name.Should().Be("workspace");
        skill.AllowedTools.Should().NotBeNull();
        skill.AllowedTools!.Should().BeEquivalentTo(new[]
        {
            "read_file", "write_file", "list_files", "run_tests", "run_lint"
        });

        skill.Egress.Should().NotBeNull(
            "the SKILL.md declares an egress: section so the resolver materialises an EgressManifest");
        skill.Egress!.Allowlist.Should().BeEmpty(
            "the workspace skill is deny-all by design — no per-skill egress entries");
        skill.Egress.HasAllowlist.Should().BeFalse();
    }

    [Fact]
    public void ShippedPluginJson_ReferencesSkillsFolder()
    {
        var pluginJsonPath = LocatePluginJson();
        File.Exists(pluginJsonPath).Should().BeTrue();
        var json = File.ReadAllText(pluginJsonPath);
        json.Should().Contain("\"name\": \"workspace-skill\"");
        json.Should().Contain("\"skills\": \"./skills/\"");
    }

    private static string LocateShippedSkillMd()
    {
        var repoRoot = FindRepoRoot();
        return Path.Combine(repoRoot, "plugins", "workspace-skill", "skills", "workspace", "SKILL.md");
    }

    private static string LocatePluginJson()
    {
        var repoRoot = FindRepoRoot();
        return Path.Combine(repoRoot, "plugins", "workspace-skill", "plugin.json");
    }

    /// <summary>
    /// Walks up from the assembly directory until it finds the repo root
    /// (identified by <c>AgenticHarness.slnx</c> in <c>src/</c>). The test
    /// must not assume the CWD because xUnit launches with a tooling-chosen
    /// working directory.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // Repo root contains either the solution file or the plugins/ folder.
            if (File.Exists(Path.Combine(dir.FullName, "src", "AgenticHarness.slnx")))
                return dir.FullName;
            if (Directory.Exists(Path.Combine(dir.FullName, "plugins", "workspace-skill")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
