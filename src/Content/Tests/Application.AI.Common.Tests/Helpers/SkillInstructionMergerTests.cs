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

    private static SkillAmendment Amendment(string skillId, string content) => new()
    {
        Id = Guid.NewGuid().ToString(),
        SkillId = skillId,
        Content = content,
        LearnedFrom = "test",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Merge_AmendmentForSkill_AppendedAfterItsInstructions()
    {
        var skills = new[] { Skill("Researcher", "Do the research.") };
        var amendments = new Dictionary<string, IReadOnlyList<SkillAmendment>>
        {
            ["Researcher"] = [Amendment("Researcher", "Always cite sources.")]
        };

        var merged = SkillInstructionMerger.Merge(
            skills, additionalContext: null, amendmentsBySkillId: amendments);

        merged.Should().Contain("Do the research.");
        merged.Should().Contain("Always cite sources.");
        merged.IndexOf("Do the research.", StringComparison.Ordinal)
            .Should().BeLessThan(
                merged.IndexOf("Always cite sources.", StringComparison.Ordinal),
                "an amendment is learned content added after the skill's own instructions, never ahead of them");
    }

    [Fact]
    public void Merge_MultipleAmendmentsForOneSkill_AllAppear()
    {
        var skills = new[] { Skill("Researcher", "Do the research.") };
        var amendments = new Dictionary<string, IReadOnlyList<SkillAmendment>>
        {
            ["Researcher"] =
            [
                Amendment("Researcher", "Always cite sources."),
                Amendment("Researcher", "Prefer primary sources over secondary ones.")
            ]
        };

        var merged = SkillInstructionMerger.Merge(
            skills, additionalContext: null, amendmentsBySkillId: amendments);

        merged.Should().Contain("Always cite sources.");
        merged.Should().Contain("Prefer primary sources over secondary ones.");
    }

    [Fact]
    public void Merge_AmendmentForOneSkillOfMany_OnlyThatSkillsBlockChanges()
    {
        var skills = new[] { Skill("Alpha", "Alpha body."), Skill("Beta", "Beta body.") };
        var amendments = new Dictionary<string, IReadOnlyList<SkillAmendment>>
        {
            ["Alpha"] = [Amendment("Alpha", "Alpha's learned note.")]
        };

        var merged = SkillInstructionMerger.Merge(
            skills, additionalContext: null, amendmentsBySkillId: amendments);

        merged.Should().Contain("Alpha's learned note.");
        // Beta's block must stay byte-identical to the no-amendments case — this is the negative
        // assertion that proves the merge is scoped per skill, not applied to whichever skill happens
        // to run first or globally to the whole merged text.
        merged.Should().Contain("## Skill: Beta\n\nBeta body.");
    }

    [Fact]
    public void Merge_NoAmendmentForSkill_UnchangedFromNoAmendmentsCase()
    {
        var skills = new[] { Skill("Researcher", "Do the research.") };
        var emptyAmendments = new Dictionary<string, IReadOnlyList<SkillAmendment>>();

        var withEmptyDict = SkillInstructionMerger.Merge(
            skills, additionalContext: null, amendmentsBySkillId: emptyAmendments);
        var withNullDict = SkillInstructionMerger.Merge(
            skills, additionalContext: null, amendmentsBySkillId: null);

        withEmptyDict.Should().Be("Do the research.");
        withEmptyDict.Should().Be(withNullDict);
    }

    [Fact]
    public void Merge_AmendmentForOnDemandDisclosedSkill_IsAlsoOmitted()
    {
        // Deferred-body skills defer their amendments too — both are Tier 2 content. Shipping the
        // amendment here while the body is deferred to load_skill would defeat the whole point of
        // disclosure: the model would see the learned note attached to instructions it hasn't loaded.
        var skills = new[] { Skill("Researcher", "Do the research.") };
        var amendments = new Dictionary<string, IReadOnlyList<SkillAmendment>>
        {
            ["Researcher"] = [Amendment("Researcher", "Always cite sources.")]
        };
        var disclosed = new HashSet<string> { "Researcher" };

        var merged = SkillInstructionMerger.Merge(
            skills, additionalContext: null, disclosedOnDemandSkillIds: disclosed, amendmentsBySkillId: amendments);

        merged.Should().BeEmpty();
    }
}
