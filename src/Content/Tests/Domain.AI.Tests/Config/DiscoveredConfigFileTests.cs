using Domain.AI.Config;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Config;

/// <summary>
/// Tests for <see cref="DiscoveredConfigFile"/> record — construction, defaults, equality.
/// </summary>
public sealed class DiscoveredConfigFileTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var file = new DiscoveredConfigFile
        {
            FilePath = "/repo/CLAUDE.md",
            Scope = ConfigScope.Project,
            Priority = 0,
            Content = "# Project Rules"
        };

        file.FilePath.Should().Be("/repo/CLAUDE.md");
        file.Scope.Should().Be(ConfigScope.Project);
        file.Priority.Should().Be(0);
        file.Content.Should().Be("# Project Rules");
    }

    [Fact]
    public void Defaults_PathGlobs_IsNull()
    {
        var file = new DiscoveredConfigFile
        {
            FilePath = "/test",
            Scope = ConfigScope.User,
            Priority = 1,
            Content = ""
        };

        file.PathGlobs.Should().BeNull();
    }

    [Fact]
    public void PathGlobs_WhenSet_RetainsValues()
    {
        var globs = new List<string> { "src/**/*.cs", "tests/**" };
        var file = new DiscoveredConfigFile
        {
            FilePath = "/repo/.claude/rules/testing.md",
            Scope = ConfigScope.Project,
            Priority = 2,
            Content = "Testing rules",
            PathGlobs = globs
        };

        file.PathGlobs.Should().BeEquivalentTo(globs);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var globs = new List<string> { "*.cs" };
        var file1 = new DiscoveredConfigFile
        {
            FilePath = "/a",
            Scope = ConfigScope.Local,
            Priority = 0,
            Content = "content",
            PathGlobs = globs
        };
        var file2 = new DiscoveredConfigFile
        {
            FilePath = "/a",
            Scope = ConfigScope.Local,
            Priority = 0,
            Content = "content",
            PathGlobs = globs
        };

        file1.Should().Be(file2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new DiscoveredConfigFile
        {
            FilePath = "/a",
            Scope = ConfigScope.User,
            Priority = 5,
            Content = "original"
        };

        var updated = original with { Content = "updated" };

        updated.Content.Should().Be("updated");
        original.Content.Should().Be("original");
    }
}
