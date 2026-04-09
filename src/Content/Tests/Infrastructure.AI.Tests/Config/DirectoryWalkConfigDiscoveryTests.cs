using Domain.AI.Config;
using FluentAssertions;
using Infrastructure.AI.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Config;

/// <summary>
/// Tests for <see cref="DirectoryWalkConfigDiscovery"/> covering directory walk,
/// include resolution, frontmatter parsing, and circular reference prevention.
/// </summary>
public sealed class DirectoryWalkConfigDiscoveryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DirectoryWalkConfigDiscovery _sut;

    public DirectoryWalkConfigDiscoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"config-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _sut = new DirectoryWalkConfigDiscovery(
            NullLogger<DirectoryWalkConfigDiscovery>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Discover_FindsAgentMd_InCurrentDir()
    {
        var agentContent = "# Agent Config\nSome instructions";
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "AGENT.md"), agentContent);

        var results = await _sut.DiscoverAsync(_tempRoot);

        results.Should().ContainSingle(f => f.FilePath.EndsWith("AGENT.md"))
            .Which.Should().Match<DiscoveredConfigFile>(f =>
                f.Scope == ConfigScope.Project &&
                f.Priority == 0 &&
                f.Content == agentContent);
    }

    [Fact]
    public async Task Discover_WalksUpward_FindsParentFiles()
    {
        var childDir = Path.Combine(_tempRoot, "child");
        Directory.CreateDirectory(childDir);

        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "CLAUDE.md"), "parent config");
        await File.WriteAllTextAsync(Path.Combine(childDir, "AGENT.md"), "child config");

        var results = await _sut.DiscoverAsync(childDir);

        results.Should().Contain(f => f.FilePath.EndsWith("AGENT.md") && f.Priority == 0);
        results.Should().Contain(f => f.FilePath.EndsWith("CLAUDE.md") && f.Priority == 1);
    }

    [Fact]
    public async Task Discover_CloserFilesHaveHigherPriority()
    {
        var child = Path.Combine(_tempRoot, "a", "b");
        Directory.CreateDirectory(child);

        await File.WriteAllTextAsync(Path.Combine(child, "CLAUDE.md"), "closest");
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "a", "CLAUDE.md"), "middle");
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "CLAUDE.md"), "farthest");

        var results = await _sut.DiscoverAsync(child);

        var claudeFiles = results.Where(f => f.FilePath.EndsWith("CLAUDE.md")).ToList();
        claudeFiles.Count.Should().BeGreaterThanOrEqualTo(3);
        claudeFiles[0].Priority.Should().BeLessThan(claudeFiles[1].Priority);
        claudeFiles[1].Priority.Should().BeLessThan(claudeFiles[2].Priority);
        claudeFiles[0].Content.Should().Be("closest");
    }

    [Fact]
    public async Task Discover_ParsesFrontmatter_PathGlobs()
    {
        var content = "---\npaths: src/**/*.cs, tests/**/*.cs\n---\n# Rules";
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "CLAUDE.md"), content);

        var results = await _sut.DiscoverAsync(_tempRoot);

        var file = results.Should().ContainSingle(f => f.FilePath.EndsWith("CLAUDE.md")).Subject;
        file.PathGlobs.Should().NotBeNull();
        file.PathGlobs.Should().BeEquivalentTo(["src/**/*.cs", "tests/**/*.cs"]);
    }

    [Fact]
    public async Task Discover_ResolvesIncludeDirective()
    {
        var includesDir = Path.Combine(_tempRoot, "includes");
        Directory.CreateDirectory(includesDir);

        var includedContent = "# Included rules\nDo the thing.";
        await File.WriteAllTextAsync(Path.Combine(includesDir, "extra.md"), includedContent);

        var mainContent = "# Main config\n@./includes/extra.md\n# End";
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "AGENT.md"), mainContent);

        var results = await _sut.DiscoverAsync(_tempRoot);

        var agent = results.Should().ContainSingle(f => f.FilePath.EndsWith("AGENT.md")).Subject;
        agent.Content.Should().Contain("Included rules");
        agent.Content.Should().Contain("Do the thing.");
        agent.Content.Should().Contain("# Main config");
        agent.Content.Should().Contain("# End");
    }

    [Fact]
    public async Task Discover_SkipsNonExistentIncludes()
    {
        var content = "# Config\n@./does-not-exist.md\n# After";
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "CLAUDE.md"), content);

        var results = await _sut.DiscoverAsync(_tempRoot);

        var file = results.Should().ContainSingle(f => f.FilePath.EndsWith("CLAUDE.md")).Subject;
        file.Content.Should().Contain("# Config");
        file.Content.Should().Contain("# After");
        file.Content.Should().NotContain("does-not-exist");
    }

    [Fact]
    public async Task Discover_PreventsCircularIncludes()
    {
        var fileA = Path.Combine(_tempRoot, "AGENT.md");
        var fileB = Path.Combine(_tempRoot, "other.md");

        await File.WriteAllTextAsync(fileA, "# A\n@./other.md\n# End A");
        await File.WriteAllTextAsync(fileB, "# B\n@./AGENT.md\n# End B");

        var results = await _sut.DiscoverAsync(_tempRoot);

        results.Should().NotBeNull();
        var agent = results.Should().ContainSingle(f => f.FilePath.EndsWith("AGENT.md")).Subject;
        agent.Content.Should().Contain("# A");
        agent.Content.Should().Contain("# B");
    }

    [Fact]
    public async Task Discover_EmptyDirectory_ReturnsEmpty()
    {
        var emptyDir = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(emptyDir);

        var results = await _sut.DiscoverAsync(emptyDir);

        var localResults = results.Where(f => f.FilePath.StartsWith(_tempRoot)).ToList();
        localResults.Should().BeEmpty();
    }

    [Fact]
    public async Task Discover_FindsRuleFiles_InClaudeRulesDirectory()
    {
        var rulesDir = Path.Combine(_tempRoot, ".claude", "rules");
        Directory.CreateDirectory(rulesDir);

        await File.WriteAllTextAsync(Path.Combine(rulesDir, "alpha.md"), "# Alpha rule");
        await File.WriteAllTextAsync(Path.Combine(rulesDir, "beta.md"), "# Beta rule");

        var results = await _sut.DiscoverAsync(_tempRoot);

        var ruleFiles = results.Where(f => f.FilePath.StartsWith(_tempRoot) && f.FilePath.Contains(".claude")).ToList();
        ruleFiles.Should().HaveCount(2);
        ruleFiles.Should().AllSatisfy(f => f.Scope.Should().Be(ConfigScope.Project));
    }

    [Fact]
    public async Task Discover_LocalMd_HasLocalScope()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "CLAUDE.local.md"), "local override");

        var results = await _sut.DiscoverAsync(_tempRoot);

        var local = results.Should().ContainSingle(f => f.FilePath.EndsWith("CLAUDE.local.md")).Subject;
        local.Scope.Should().Be(ConfigScope.Local);
    }

    [Fact]
    public async Task Discover_FrontmatterWithoutPaths_ReturnsNullGlobs()
    {
        var content = "---\ntitle: My Config\nauthor: test\n---\n# Content";
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "CLAUDE.md"), content);

        var results = await _sut.DiscoverAsync(_tempRoot);

        var file = results.Should().ContainSingle(f => f.FilePath.EndsWith("CLAUDE.md")).Subject;
        file.PathGlobs.Should().BeNull();
    }

    [Fact]
    public async Task Discover_NoFrontmatter_ReturnsNullGlobs()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "AGENT.md"), "# Just content, no frontmatter");

        var results = await _sut.DiscoverAsync(_tempRoot);

        var file = results.Should().ContainSingle(f => f.FilePath.EndsWith("AGENT.md")).Subject;
        file.PathGlobs.Should().BeNull();
    }
}
