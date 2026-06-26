using Application.AI.Common.CQRS.SkillTraining.GateCandidateSkill;
using Domain.AI.SkillTraining;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.SkillTraining;

public sealed class GateCandidateSkillCommandValidatorTests
{
    private readonly GateCandidateSkillCommandValidator _sut = new();

    private static GateCandidateSkillCommand Valid() => new()
    {
        CandidateSkill = "c",
        CandidateHard = 0.5,
        CandidateSoft = 0.5,
        CurrentSkill = "cur",
        CurrentScore = 0.5,
        BestSkill = "best",
        BestScore = 0.5,
        BestStep = 0,
        GlobalStep = 0,
        Metric = GateMetric.Hard
    };

    [Fact]
    public void Valid_command_has_no_errors()
    {
        _sut.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void CandidateHard_OutOfUnitInterval_Fails(double bad)
    {
        var cmd = Valid() with { CandidateHard = bad };
        _sut.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.CandidateHard);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void CandidateSoft_OutOfUnitInterval_Fails(double bad)
    {
        var cmd = Valid() with { CandidateSoft = bad };
        _sut.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.CandidateSoft);
    }

    [Fact]
    public void Negative_BestStep_Fails()
    {
        var cmd = Valid() with { BestStep = -1 };
        _sut.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.BestStep);
    }

    [Fact]
    public void Negative_GlobalStep_Fails()
    {
        var cmd = Valid() with { GlobalStep = -1 };
        _sut.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.GlobalStep);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void MixedWeight_OutOfUnitInterval_Fails(double bad)
    {
        var cmd = Valid() with { MixedWeight = bad };
        _sut.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.MixedWeight);
    }

    [Fact]
    public void BestStep_Exceeds_GlobalStep_Fails()
    {
        var cmd = Valid() with { BestStep = 5, GlobalStep = 2 };
        Assert.False(_sut.TestValidate(cmd).IsValid);
    }

    [Fact]
    public void Skill_LongerThanMax_Fails()
    {
        var oversize = new string('x', GateCandidateSkillCommandValidator.MaxSkillLength + 1);
        var cmd = Valid() with { CandidateSkill = oversize };
        _sut.TestValidate(cmd).ShouldHaveValidationErrorFor(c => c.CandidateSkill);
    }

    [Fact]
    public void Skill_AtExactlyMax_Passes()
    {
        var atMax = new string('x', GateCandidateSkillCommandValidator.MaxSkillLength);
        var cmd = Valid() with
        {
            CandidateSkill = atMax,
            CurrentSkill = atMax,
            BestSkill = atMax
        };
        _sut.TestValidate(cmd).ShouldNotHaveValidationErrorFor(c => c.CandidateSkill);
    }
}
