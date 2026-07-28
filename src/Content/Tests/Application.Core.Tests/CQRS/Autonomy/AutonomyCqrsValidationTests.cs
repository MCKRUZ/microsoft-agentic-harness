using Application.Core.CQRS.Autonomy;
using Domain.AI.Agents;
using Domain.AI.Changes;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS.Autonomy;

/// <summary>
/// Validator tests for the autonomy governance read surface: required fields, length caps, and
/// the 400-vs-404 boundary — malformed blast radius / target kind names fail validation, while
/// a well-formed-but-unknown subagent type passes (its 404 belongs to the handler).
/// </summary>
public sealed class AutonomyCqrsValidationTests
{
    private readonly GetAutonomyTierQueryValidator _tierValidator = new();
    private readonly PreviewAutonomyDecisionQueryValidator _previewValidator = new();

    private static PreviewAutonomyDecisionQuery ValidPreview() => new()
    {
        SubagentType = nameof(SubagentType.Explore),
        BlastRadius = nameof(BlastRadius.Low),
        TargetKind = nameof(ChangeTargetKind.GitRepo),
        IsStateChange = true,
        SkillKey = "skill.demo",
    };

    [Fact]
    public void TierValidator_ValidQuery_Passes()
    {
        var result = _tierValidator.Validate(
            new GetAutonomyTierQuery { SubagentType = nameof(SubagentType.Explore) });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void TierValidator_UnknownButWellFormedName_Passes()
    {
        // Existence is the handler's concern (404, not 400).
        var result = _tierValidator.Validate(
            new GetAutonomyTierQuery { SubagentType = "NotARealType" });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void TierValidator_EmptySubagentType_Fails()
    {
        var result = _tierValidator.Validate(new GetAutonomyTierQuery { SubagentType = "" });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void TierValidator_OverlongSubagentType_Fails()
    {
        var result = _tierValidator.Validate(new GetAutonomyTierQuery
        {
            SubagentType = new string('a', AutonomyValidationRules.MaxEnumNameLength + 1),
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void PreviewValidator_ValidQuery_Passes()
    {
        var result = _previewValidator.Validate(ValidPreview());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void PreviewValidator_CaseInsensitiveEnumNames_Passes()
    {
        var result = _previewValidator.Validate(ValidPreview() with
        {
            BlastRadius = "medium",
            TargetKind = "gitrepo",
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void PreviewValidator_EmptySubagentType_Fails()
    {
        var result = _previewValidator.Validate(ValidPreview() with { SubagentType = "" });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void PreviewValidator_EmptyBlastRadius_Fails()
    {
        var result = _previewValidator.Validate(ValidPreview() with { BlastRadius = "" });

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("NotARadius")]
    [InlineData("3")] // numeric forms are not part of the wire contract
    public void PreviewValidator_MalformedBlastRadius_Fails(string blastRadius)
    {
        var result = _previewValidator.Validate(ValidPreview() with { BlastRadius = blastRadius });

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("NotAKind")]
    [InlineData("1")]
    public void PreviewValidator_MalformedTargetKind_Fails(string targetKind)
    {
        var result = _previewValidator.Validate(ValidPreview() with { TargetKind = targetKind });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void PreviewValidator_OverlongSkillKey_Fails()
    {
        var result = _previewValidator.Validate(ValidPreview() with
        {
            SkillKey = new string('k', AutonomyValidationRules.MaxSkillKeyLength + 1),
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void PreviewValidator_NullSkillKey_Passes()
    {
        var result = _previewValidator.Validate(ValidPreview() with { SkillKey = null });

        result.IsValid.Should().BeTrue();
    }
}
