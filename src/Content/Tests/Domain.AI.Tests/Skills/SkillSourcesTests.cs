using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillSources"/> constants — values are correct and non-empty.
/// </summary>
public sealed class SkillSourcesTests
{
    [Fact]
    public void Bundled_HasExpectedValue()
    {
        SkillSources.Bundled.Should().Be("bundled");
    }

    [Fact]
    public void Filesystem_HasExpectedValue()
    {
        SkillSources.Filesystem.Should().Be("filesystem");
    }

    [Fact]
    public void Mcp_HasExpectedValue()
    {
        SkillSources.Mcp.Should().Be("mcp");
    }

    [Fact]
    public void Plugin_HasExpectedValue()
    {
        SkillSources.Plugin.Should().Be("plugin");
    }

    [Fact]
    public void Inline_HasExpectedValue()
    {
        SkillSources.Inline.Should().Be("inline");
    }

    [Fact]
    public void AllConstants_AreNonEmpty()
    {
        SkillSources.Bundled.Should().NotBeNullOrWhiteSpace();
        SkillSources.Filesystem.Should().NotBeNullOrWhiteSpace();
        SkillSources.Mcp.Should().NotBeNullOrWhiteSpace();
        SkillSources.Plugin.Should().NotBeNullOrWhiteSpace();
        SkillSources.Inline.Should().NotBeNullOrWhiteSpace();
    }
}
