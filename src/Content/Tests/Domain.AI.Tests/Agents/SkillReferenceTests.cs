using Domain.AI.Agents;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Agents;

/// <summary>
/// Tests for <see cref="SkillReference"/> — defaults, computed properties, and property assignment.
/// </summary>
public sealed class SkillReferenceTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var skill = new SkillReference();

        skill.Id.Should().BeEmpty();
        skill.Name.Should().BeEmpty();
        skill.Description.Should().BeEmpty();
        skill.Path.Should().BeEmpty();
        skill.IsRequired.Should().BeTrue();
        skill.ActivityType.Should().BeNull();
        skill.DependsOn.Should().BeEmpty();
    }

    [Fact]
    public void HasDescription_EmptyDescription_ReturnsFalse()
    {
        var skill = new SkillReference();

        skill.HasDescription.Should().BeFalse();
    }

    [Fact]
    public void HasDescription_WithDescription_ReturnsTrue()
    {
        var skill = new SkillReference { Description = "Performs analysis" };

        skill.HasDescription.Should().BeTrue();
    }

    [Fact]
    public void IsOptional_RequiredTrue_ReturnsFalse()
    {
        var skill = new SkillReference { IsRequired = true };

        skill.IsOptional.Should().BeFalse();
    }

    [Fact]
    public void IsOptional_RequiredFalse_ReturnsTrue()
    {
        var skill = new SkillReference { IsRequired = false };

        skill.IsOptional.Should().BeTrue();
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var deps = new List<string> { "skill-a", "skill-b" };
        var skill = new SkillReference
        {
            Id = "research-skill",
            Name = "Research",
            Description = "Conducts research",
            Path = "skills/research/SKILL.md",
            IsRequired = false,
            ActivityType = "research",
            DependsOn = deps
        };

        skill.Id.Should().Be("research-skill");
        skill.Name.Should().Be("Research");
        skill.Description.Should().Be("Conducts research");
        skill.Path.Should().Be("skills/research/SKILL.md");
        skill.IsRequired.Should().BeFalse();
        skill.ActivityType.Should().Be("research");
        skill.DependsOn.Should().BeEquivalentTo(deps);
    }
}
