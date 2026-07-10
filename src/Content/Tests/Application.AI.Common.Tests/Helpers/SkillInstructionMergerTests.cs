using Application.AI.Common.Helpers;
using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Helpers;

/// <summary>
/// Unit tests for <see cref="SkillInstructionMerger"/>. Covers agent-level instruction
/// precedence (the agent's own system prompt leads the merged text) and the backward-compatible
/// skill-only behaviour when no agent instructions are supplied.
/// </summary>
public sealed class SkillInstructionMergerTests
{
    private static SkillDefinition Skill(string name, string? instructions) =>
        new() { Id = name, Name = name, Instructions = instructions };

    [Fact]
    public void Merge_WithAgentInstructions_PrependsThemAheadOfSkillContent()
    {
        var skills = new[] { Skill("Researcher", "Do the research.") };

        var merged = SkillInstructionMerger.Merge(
            skills, additionalContext: null, agentInstructions: "You are a careful analyst.");

        merged.Should().StartWith("You are a careful analyst.");
        merged.Should().Contain("Do the research.");
        merged.IndexOf("You are a careful analyst.", StringComparison.Ordinal)
            .Should().BeLessThan(
                merged.IndexOf("Do the research.", StringComparison.Ordinal),
                "the agent's own instructions must lead the merged system prompt, ahead of its skills");
    }

    [Fact]
    public void Merge_WithoutAgentInstructions_PreservesSkillOnlyBehaviour()
    {
        var skills = new[] { Skill("Researcher", "Do the research.") };

        var merged = SkillInstructionMerger.Merge(skills, additionalContext: null);

        merged.Should().Be("Do the research.");
    }

    [Fact]
    public void Merge_AgentInstructionsWithMultipleSkills_LeadsThenHeaderedSkills()
    {
        var skills = new[]
        {
            Skill("Alpha", "Alpha body."),
            Skill("Beta", "Beta body."),
        };

        var merged = SkillInstructionMerger.Merge(
            skills, additionalContext: null, agentInstructions: "Agent lead.");

        merged.Should().StartWith("Agent lead.");
        merged.Should().Contain("## Skill: Alpha");
        merged.Should().Contain("## Skill: Beta");
    }

    [Fact]
    public void Merge_EmptyAgentInstructions_IsIgnored()
    {
        var skills = new[] { Skill("Researcher", "Do the research.") };

        var merged = SkillInstructionMerger.Merge(
            skills, additionalContext: null, agentInstructions: "   ");

        merged.Should().Be("Do the research.");
    }
}
