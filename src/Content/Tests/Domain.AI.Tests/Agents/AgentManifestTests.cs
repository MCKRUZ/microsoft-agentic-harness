using Domain.AI.Agents;
using Domain.AI.Tools;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Agents;

/// <summary>
/// Tests for <see cref="AgentManifest"/> — defaults, computed properties, and OrdinalNumber parsing.
/// </summary>
public sealed class AgentManifestTests
{
    [Fact]
    public void Defaults_AllProperties_AreCorrect()
    {
        var manifest = new AgentManifest();

        manifest.Id.Should().BeEmpty();
        manifest.Name.Should().BeEmpty();
        manifest.Description.Should().BeEmpty();
        manifest.Version.Should().BeNull();
        manifest.Author.Should().BeNull();
        manifest.Domain.Should().BeNull();
        manifest.Category.Should().BeNull();
        manifest.Tags.Should().BeEmpty();
        manifest.Instructions.Should().BeNull();
        manifest.AllowedTools.Should().BeNull();
        manifest.ToolDeclarations.Should().BeNull();
        manifest.StateConfiguration.Should().BeNull();
        manifest.DecisionFramework.Should().BeNull();
        manifest.Skills.Should().BeEmpty();
        manifest.FilePath.Should().BeEmpty();
        manifest.BaseDirectory.Should().BeEmpty();
        manifest.Metadata.Should().BeNull();
    }

    [Fact]
    public void HasSkills_NoSkills_ReturnsFalse()
    {
        var manifest = new AgentManifest();

        manifest.HasSkills.Should().BeFalse();
    }

    [Fact]
    public void HasSkills_WithSkills_ReturnsTrue()
    {
        var manifest = new AgentManifest
        {
            Skills = [new SkillReference { Id = "skill-1" }]
        };

        manifest.HasSkills.Should().BeTrue();
    }

    [Fact]
    public void HasToolRestrictions_NullAllowedTools_ReturnsFalse()
    {
        var manifest = new AgentManifest { AllowedTools = null };

        manifest.HasToolRestrictions.Should().BeFalse();
    }

    [Fact]
    public void HasToolRestrictions_EmptyAllowedTools_ReturnsFalse()
    {
        var manifest = new AgentManifest { AllowedTools = new List<string>() };

        manifest.HasToolRestrictions.Should().BeFalse();
    }

    [Fact]
    public void HasToolRestrictions_WithAllowedTools_ReturnsTrue()
    {
        var manifest = new AgentManifest
        {
            AllowedTools = new List<string> { "file_system" }
        };

        manifest.HasToolRestrictions.Should().BeTrue();
    }

    [Fact]
    public void HasToolDeclarations_Null_ReturnsFalse()
    {
        var manifest = new AgentManifest { ToolDeclarations = null };

        manifest.HasToolDeclarations.Should().BeFalse();
    }

    [Fact]
    public void HasToolDeclarations_WithDeclarations_ReturnsTrue()
    {
        var manifest = new AgentManifest
        {
            ToolDeclarations = [new ToolDeclaration { Name = "bash" }]
        };

        manifest.HasToolDeclarations.Should().BeTrue();
    }

    [Fact]
    public void HasStateConfiguration_Null_ReturnsFalse()
    {
        var manifest = new AgentManifest();

        manifest.HasStateConfiguration.Should().BeFalse();
    }

    [Fact]
    public void HasDecisionFramework_Null_ReturnsFalse()
    {
        var manifest = new AgentManifest();

        manifest.HasDecisionFramework.Should().BeFalse();
    }

    [Theory]
    [InlineData("phase0-discovery", 0)]
    [InlineData("phase1-analysis", 1)]
    [InlineData("step2-review", 2)]
    [InlineData("stage10-deploy", 10)]
    [InlineData("PHASE3-Test", 3)]
    public void OrdinalNumber_WithValidPattern_ExtractsNumber(string id, int expected)
    {
        var manifest = new AgentManifest { Id = id };

        manifest.OrdinalNumber.Should().Be(expected);
    }

    [Theory]
    [InlineData("research-agent")]
    [InlineData("simple")]
    [InlineData("no-number")]
    [InlineData("")]
    public void OrdinalNumber_WithoutPattern_ReturnsNegativeOne(string id)
    {
        var manifest = new AgentManifest { Id = id };

        manifest.OrdinalNumber.Should().Be(-1);
    }

    [Fact]
    public void LoadedAt_DefaultsToUtcNow()
    {
        var before = DateTime.UtcNow;
        var manifest = new AgentManifest();
        var after = DateTime.UtcNow;

        manifest.LoadedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
