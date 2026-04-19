using Domain.AI.Skills;
using Domain.Common.Config.AI;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillResource"/> — defaults, IsLoaded, property assignment.
/// </summary>
public sealed class SkillResourceTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var resource = new SkillResource();

        resource.FileName.Should().BeEmpty();
        resource.FilePath.Should().BeEmpty();
        resource.RelativePath.Should().BeEmpty();
        resource.Content.Should().BeNull();
    }

    [Fact]
    public void IsLoaded_NullContent_ReturnsFalse()
    {
        var resource = new SkillResource { Content = null };

        resource.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public void IsLoaded_WithContent_ReturnsTrue()
    {
        var resource = new SkillResource { Content = "file content" };

        resource.IsLoaded.Should().BeTrue();
    }

    [Fact]
    public void IsLoaded_EmptyStringContent_ReturnsTrue()
    {
        var resource = new SkillResource { Content = "" };

        resource.IsLoaded.Should().BeTrue();
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var resource = new SkillResource
        {
            FileName = "output.template.md",
            FilePath = "/skills/research/templates/output.template.md",
            RelativePath = "templates/output.template.md",
            ResourceType = SkillResourceType.Template,
            Content = "# Template Content"
        };

        resource.FileName.Should().Be("output.template.md");
        resource.FilePath.Should().Be("/skills/research/templates/output.template.md");
        resource.RelativePath.Should().Be("templates/output.template.md");
        resource.ResourceType.Should().Be(SkillResourceType.Template);
        resource.Content.Should().Be("# Template Content");
    }
}
