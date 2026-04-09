using Application.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Application.Common.Tests.Helpers;

public class YamlFrontmatterHelperTests
{
    [Fact]
    public void HasFrontmatter_ValidFrontmatter_ReturnsTrue()
    {
        var markdown = """
            ---
            name: test-skill
            ---
            # Body
            """;

        YamlFrontmatterHelper.HasFrontmatter(markdown).Should().BeTrue();
    }

    [Fact]
    public void HasFrontmatter_NoFrontmatter_ReturnsFalse()
    {
        var markdown = "# Just a heading\nSome content.";

        YamlFrontmatterHelper.HasFrontmatter(markdown).Should().BeFalse();
    }

    [Fact]
    public void HasFrontmatter_NullInput_ReturnsFalse()
    {
        YamlFrontmatterHelper.HasFrontmatter(null).Should().BeFalse();
    }

    [Fact]
    public void HasFrontmatter_EmptyString_ReturnsFalse()
    {
        YamlFrontmatterHelper.HasFrontmatter("").Should().BeFalse();
    }

    [Fact]
    public void ExtractFrontmatter_ValidContent_ReturnsParts()
    {
        var markdown = """
            ---
            name: code-review
            effort: medium
            ---
            # Code Review Skill
            Reviews code for quality.
            """;

        var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter(markdown);

        yaml.Should().Contain("name: code-review");
        yaml.Should().Contain("effort: medium");
        body.Should().Contain("# Code Review Skill");
    }

    [Fact]
    public void ExtractFrontmatter_NullInput_ReturnsEmptyYamlAndEmptyBody()
    {
        var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter(null);

        yaml.Should().BeEmpty();
        body.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFrontmatter_EmptyString_ReturnsEmptyYamlAndEmptyBody()
    {
        var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter("");

        yaml.Should().BeEmpty();
        body.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFrontmatter_NoClosingDelimiter_ReturnsOriginalAsBody()
    {
        var markdown = "---\nname: broken\nno closing";

        var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter(markdown);

        yaml.Should().BeEmpty();
        body.Should().Be(markdown);
    }

    [Fact]
    public void ExtractFrontmatter_NoFrontmatter_ReturnsOriginalAsBody()
    {
        var markdown = "# Heading\nSome content.";

        var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter(markdown);

        yaml.Should().BeEmpty();
        body.Should().Be(markdown);
    }
}
