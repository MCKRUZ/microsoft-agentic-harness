using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillDefinition"/> token estimation computed properties.
/// </summary>
public sealed class SkillDefinitionTokenEstimateTests
{
    [Fact]
    public void Level1TokenEstimate_EmptySkill_ReturnsZero()
    {
        var skill = new SkillDefinition();

        skill.Level1TokenEstimate.Should().Be(0);
    }

    [Fact]
    public void Level1TokenEstimate_WithMetadata_ComputesEstimate()
    {
        var skill = new SkillDefinition
        {
            Id = "research",       // 8 chars => (8+3)/4 = 2
            Name = "Research Skill", // 14 chars => (14+3)/4 = 4
            Description = "Desc",   // 4 chars => (4+3)/4 = 1
            Category = "analysis",  // 8 chars => (8+3)/4 = 2
            Tags = ["tag1", "tag2"] // 4+4 chars => 1+1 = 2
        };

        skill.Level1TokenEstimate.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Level2TokenEstimate_EmptyInstructions_ReturnsZero()
    {
        var skill = new SkillDefinition { Instructions = "" };

        skill.Level2TokenEstimate.Should().Be(0);
    }

    [Fact]
    public void Level2TokenEstimate_WithInstructions_ComputesEstimate()
    {
        // 100 chars => (100+3)/4 = 25 tokens
        var instructions = new string('a', 100);
        var skill = new SkillDefinition { Instructions = instructions };

        skill.Level2TokenEstimate.Should().Be(25);
    }

    [Fact]
    public void Level2TokenEstimate_IncludesObjectivesAndTraceFormat()
    {
        var skill = new SkillDefinition
        {
            Instructions = new string('a', 100), // 25 tokens
            Objectives = new string('b', 40),     // (40+3)/4 = 10
            TraceFormat = new string('c', 20)     // (20+3)/4 = 5
        };

        skill.Level2TokenEstimate.Should().Be(40); // 25 + 10 + 5
    }

    [Fact]
    public void IsLevel2Oversized_Under5000_ReturnsFalse()
    {
        var skill = new SkillDefinition { Instructions = new string('a', 100) };

        skill.IsLevel2Oversized.Should().BeFalse();
    }

    [Fact]
    public void IsLevel2Oversized_Over5000_ReturnsTrue()
    {
        // Need > 5000 tokens => > 20000 chars
        var skill = new SkillDefinition { Instructions = new string('a', 20004) };

        skill.IsLevel2Oversized.Should().BeTrue();
    }

    [Fact]
    public void Level3LoadedTokenEstimate_NoLoadedResources_ReturnsZero()
    {
        var skill = new SkillDefinition
        {
            Templates = [new SkillResource { FileName = "t.md" }], // not loaded
            References = [new SkillResource { FileName = "r.md" }]  // not loaded
        };

        skill.Level3LoadedTokenEstimate.Should().Be(0);
    }

    [Fact]
    public void Level3LoadedTokenEstimate_WithLoadedResources_ComputesEstimate()
    {
        var skill = new SkillDefinition
        {
            Templates = [new SkillResource { FileName = "t.md", Content = new string('x', 40) }],
            References = [new SkillResource { FileName = "r.md", Content = new string('y', 20) }],
            Assets = [new SkillResource { FileName = "a.json", Content = new string('z', 12) }]
        };

        // (40+3)/4 + (20+3)/4 + (12+3)/4 = 10 + 5 + 3 = 18
        skill.Level3LoadedTokenEstimate.Should().Be(18);
    }

    [Fact]
    public void TotalLoadedTokenEstimate_SumsAllLevels()
    {
        var skill = new SkillDefinition
        {
            Id = new string('a', 13),          // (13+3)/4 = 4
            Name = "",
            Description = "",
            Instructions = new string('b', 97), // (97+3)/4 = 25
            Templates = [new SkillResource { FileName = "t", Content = new string('c', 37) }] // (37+3)/4 = 10
        };

        skill.TotalLoadedTokenEstimate.Should().Be(skill.Level1TokenEstimate + skill.Level2TokenEstimate + skill.Level3LoadedTokenEstimate);
    }

    [Fact]
    public void TotalResourceCount_AllCategories_SumsCorrectly()
    {
        var skill = new SkillDefinition
        {
            Templates = [new SkillResource(), new SkillResource()],
            References = [new SkillResource()],
            Scripts = [new SkillResource(), new SkillResource(), new SkillResource()],
            Assets = [new SkillResource()]
        };

        skill.TotalResourceCount.Should().Be(7);
    }

    [Fact]
    public void TotalResourceCount_AllNull_ReturnsZero()
    {
        // Templates and References default to empty lists, but Scripts/Assets might be null
        var skill = new SkillDefinition();

        skill.TotalResourceCount.Should().Be(0);
    }
}
