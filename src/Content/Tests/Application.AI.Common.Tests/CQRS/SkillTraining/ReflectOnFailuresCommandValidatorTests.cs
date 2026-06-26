using Application.AI.Common.CQRS.SkillTraining.ReflectOnFailures;
using Domain.AI.SkillTraining;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.SkillTraining;

public sealed class ReflectOnFailuresCommandValidatorTests
{
    private readonly ReflectOnFailuresCommandValidator _sut = new();

    private static RolloutResult Failure(string id = "f1") => new()
    {
        ItemId = id, Hard = 0.0, Soft = 0.2
    };

    private static RolloutResult Success(string id = "s1") => new()
    {
        ItemId = id, Hard = 1.0, Soft = 1.0
    };

    private static ReflectOnFailuresCommand Valid() => new()
    {
        CurrentSkill = "# skill",
        Rollouts = [Failure()]
    };

    [Fact]
    public void Valid_command_has_no_errors()
    {
        _sut.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Empty_CurrentSkill_fails()
    {
        _sut.TestValidate(Valid() with { CurrentSkill = "" })
            .ShouldHaveValidationErrorFor(c => c.CurrentSkill);
    }

    [Fact]
    public void Whitespace_CurrentSkill_fails()
    {
        _sut.TestValidate(Valid() with { CurrentSkill = "   " })
            .ShouldHaveValidationErrorFor(c => c.CurrentSkill);
    }

    [Fact]
    public void Empty_rollouts_fails()
    {
        _sut.TestValidate(Valid() with { Rollouts = [] })
            .ShouldHaveValidationErrorFor(c => c.Rollouts);
    }

    [Fact]
    public void Rollout_with_score_out_of_range_fails()
    {
        var bad = new RolloutResult { ItemId = "x", Hard = 1.5, Soft = 0.5 };
        var result = _sut.TestValidate(Valid() with { Rollouts = [bad] });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void IncludeSuccessesFalse_WithAllSuccesses_fails()
    {
        var cmd = Valid() with
        {
            IncludeSuccesses = false,
            Rollouts = [Success("s1"), Success("s2")]
        };

        Assert.False(_sut.TestValidate(cmd).IsValid);
    }

    [Fact]
    public void IncludeSuccessesFalse_WithAtLeastOneFailure_passes()
    {
        var cmd = Valid() with
        {
            IncludeSuccesses = false,
            Rollouts = [Success("s1"), Failure("f1")]
        };

        _sut.TestValidate(cmd).ShouldNotHaveAnyValidationErrors();
    }
}
