using Application.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Application.Common.Tests.Helpers;

/// <summary>
/// Regression tests for the solution-review finding that the closing frontmatter
/// delimiter was matched as a substring anywhere in the text rather than anchored
/// to a line that is exactly <c>---</c>. A <c>---</c> appearing inside a YAML value
/// previously truncated the frontmatter and leaked the real keys into the body.
/// </summary>
public class YamlFrontmatterHelperSolutionReviewFixTests
{
    [Fact]
    public void ExtractFrontmatter_InlineTripleDashInValue_DoesNotTruncateFrontmatter()
    {
        var markdown = """
            ---
            name: code-review
            description: compare A --- B variants
            effort: medium
            ---

            # Body
            Content here.
            """;

        var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter(markdown);

        yaml.Should().Contain("description: compare A --- B variants");
        yaml.Should().Contain("effort: medium");
        yaml.Should().NotContain("# Body");
        body.Should().Contain("# Body");
        body.Should().Contain("Content here.");
    }

    [Fact]
    public void HasFrontmatter_InlineTripleDashNoRealClosingDelimiter_ReturnsFalse()
    {
        var markdown = """
            ---
            description: compare A --- B variants
            """;

        YamlFrontmatterHelper.HasFrontmatter(markdown).Should().BeFalse();
    }

    [Fact]
    public void ExtractFrontmatter_ClosingDelimiterWithTrailingWhitespace_IsRecognized()
    {
        var markdown = "---\nname: skill\n---  \n# Body";

        var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter(markdown);

        yaml.Should().Be("name: skill");
        body.Should().Contain("# Body");
    }
}
