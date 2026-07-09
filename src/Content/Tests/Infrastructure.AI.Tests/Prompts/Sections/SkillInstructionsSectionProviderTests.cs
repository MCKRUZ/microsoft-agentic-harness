using Application.AI.Common.Interfaces.Prompts;
using Application.AI.Common.Services.Prompts;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts.Sections;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts.Sections;

/// <summary>
/// Unit tests for <see cref="SkillInstructionsSectionProvider"/> — the section that surfaces the
/// merged skill instructions stamped onto the scoped <see cref="ISkillInstructionAccessor"/>.
/// </summary>
public sealed class SkillInstructionsSectionProviderTests
{
    private static SkillInstructionsSectionProvider CreateProvider(string? instructions)
    {
        var accessor = new SkillInstructionAccessor();
        accessor.Set(instructions);
        return new SkillInstructionsSectionProvider(accessor);
    }

    [Fact]
    public void SectionType_IsSkillInstructions()
    {
        CreateProvider("x").SectionType.Should().Be(SystemPromptSectionType.SkillInstructions);
    }

    [Fact]
    public async Task GetSectionAsync_WithInstructions_ReturnsSectionWithVerbatimContent()
    {
        var provider = CreateProvider("You are a research agent.\n\nDo the thing.");

        var section = await provider.GetSectionAsync("ResearchAgent");

        section.Should().NotBeNull();
        section!.Content.Should().Be("You are a research agent.\n\nDo the thing.");
        section.Type.Should().Be(SystemPromptSectionType.SkillInstructions);
        section.Priority.Should().Be(20);
        section.IsCacheable.Should().BeFalse("skill instructions derive from per-request scoped state");
        section.EstimatedTokens.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetSectionAsync_NoInstructions_ReturnsNull(string? instructions)
    {
        var provider = CreateProvider(instructions);

        var section = await provider.GetSectionAsync("AnyAgent");

        section.Should().BeNull();
    }

    [Fact]
    public void Constructor_NullAccessor_Throws()
    {
        var act = () => new SkillInstructionsSectionProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
