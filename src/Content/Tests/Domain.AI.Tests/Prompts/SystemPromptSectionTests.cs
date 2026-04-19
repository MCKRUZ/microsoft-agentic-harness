using Domain.AI.Prompts;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Prompts;

/// <summary>
/// Tests for <see cref="SystemPromptSection"/> and <see cref="SystemPromptSectionType"/>.
/// </summary>
public sealed class SystemPromptSectionTests
{
    [Fact]
    public void Constructor_AllParameters_SetsValues()
    {
        var section = new SystemPromptSection(
            "Agent Identity",
            SystemPromptSectionType.AgentIdentity,
            Priority: 0,
            IsCacheable: true,
            EstimatedTokens: 200,
            Content: "You are a research agent.");

        section.Name.Should().Be("Agent Identity");
        section.Type.Should().Be(SystemPromptSectionType.AgentIdentity);
        section.Priority.Should().Be(0);
        section.IsCacheable.Should().BeTrue();
        section.EstimatedTokens.Should().Be(200);
        section.Content.Should().Be("You are a research agent.");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var s1 = new SystemPromptSection("a", SystemPromptSectionType.ToolSchemas, 1, false, 100, "content");
        var s2 = new SystemPromptSection("a", SystemPromptSectionType.ToolSchemas, 1, false, 100, "content");

        s1.Should().Be(s2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new SystemPromptSection("a", SystemPromptSectionType.UserContext, 5, true, 50, "original");
        var updated = original with { Content = "updated" };

        updated.Content.Should().Be("updated");
        original.Content.Should().Be("original");
    }

    [Theory]
    [InlineData(SystemPromptSectionType.AgentIdentity, 0)]
    [InlineData(SystemPromptSectionType.SkillInstructions, 1)]
    [InlineData(SystemPromptSectionType.ToolSchemas, 2)]
    [InlineData(SystemPromptSectionType.PermissionRules, 3)]
    [InlineData(SystemPromptSectionType.GitContext, 4)]
    [InlineData(SystemPromptSectionType.UserContext, 5)]
    [InlineData(SystemPromptSectionType.SessionState, 6)]
    [InlineData(SystemPromptSectionType.ActiveHooks, 7)]
    [InlineData(SystemPromptSectionType.CustomSection, 8)]
    public void SystemPromptSectionType_Values_HaveExpectedIntegers(
        SystemPromptSectionType value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void SystemPromptSectionType_HasExactlyNineValues()
    {
        Enum.GetValues<SystemPromptSectionType>().Should().HaveCount(9);
    }
}
