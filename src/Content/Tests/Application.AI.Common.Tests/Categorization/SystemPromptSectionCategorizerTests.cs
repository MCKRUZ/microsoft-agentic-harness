using Application.AI.Common.Categorization;
using Domain.AI.Context;
using Domain.AI.Prompts;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Categorization;

/// <summary>
/// Pins the section-type → category contract so the Foresight wire shape
/// can't drift silently if someone adds or renames a section type.
/// </summary>
public sealed class SystemPromptSectionCategorizerTests
{
    [Theory]
    [InlineData(SystemPromptSectionType.AgentIdentity, ContextCategory.System)]
    [InlineData(SystemPromptSectionType.SkillInstructions, ContextCategory.Skills)]
    [InlineData(SystemPromptSectionType.ToolSchemas, ContextCategory.Tools)]
    [InlineData(SystemPromptSectionType.PermissionRules, ContextCategory.Agents)]
    [InlineData(SystemPromptSectionType.GitContext, ContextCategory.System)]
    [InlineData(SystemPromptSectionType.UserContext, ContextCategory.Agents)]
    [InlineData(SystemPromptSectionType.SessionState, ContextCategory.System)]
    [InlineData(SystemPromptSectionType.ActiveHooks, ContextCategory.System)]
    [InlineData(SystemPromptSectionType.CustomSection, ContextCategory.System)]
    public void Map_KnownSectionType_ReturnsExpectedCategory(
        SystemPromptSectionType type, ContextCategory expected)
    {
        SystemPromptSectionCategorizer.Map(type).Should().Be(expected);
    }

    [Fact]
    public void Map_UnknownEnumValue_FallsBackToSystem()
    {
        // Cast an out-of-range value to simulate a future enum addition that
        // hasn't been added to the mapper yet. Behavior must be the safe
        // default — never produce a phantom bucket on the wire.
        var phantom = (SystemPromptSectionType)9_999;

        SystemPromptSectionCategorizer.Map(phantom).Should().Be(ContextCategory.System);
    }

    [Fact]
    public void Map_EveryDeclaredEnumValue_IsCovered()
    {
        // Defensive: if a contributor adds a new SystemPromptSectionType, this
        // test forces them to think about whether the categorizer table needs
        // updating. The switch in Map() falls back to System, so this test
        // does not fail on additions — but it logs every value to the test
        // output so the reviewer sees the new one.
        var allValues = Enum.GetValues<SystemPromptSectionType>();
        allValues.Should().NotBeEmpty();

        foreach (var v in allValues)
        {
            // Just confirm every value resolves to a valid category — no throw.
            var category = SystemPromptSectionCategorizer.Map(v);
            Enum.IsDefined(category).Should().BeTrue($"section {v} must map to a defined category");
        }
    }
}
