using Application.Core.CQRS.Skills.RefreshSkillRegistry;
using FluentAssertions;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Skills.RefreshSkillRegistry;

/// <summary>Tests for <see cref="RefreshSkillRegistryCommandValidator"/>.</summary>
public sealed class RefreshSkillRegistryCommandValidatorTests
{
    private readonly RefreshSkillRegistryCommandValidator _validator = new();

    [Fact]
    public void Validate_BlankCallerId_Fails()
    {
        var result = _validator.TestValidate(new RefreshSkillRegistryCommand { CallerId = "" });

        result.ShouldHaveValidationErrorFor(c => c.CallerId);
    }

    [Fact]
    public void Validate_CallerIdOverMaxLength_Fails()
    {
        var command = new RefreshSkillRegistryCommand
        {
            CallerId = new string('a', SkillRegistryValidationRules.MaxCallerIdLength + 1),
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.CallerId);
    }

    [Fact]
    public void Validate_ValidCallerId_Passes()
    {
        var result = _validator.TestValidate(new RefreshSkillRegistryCommand { CallerId = "ops@contoso.com" });

        result.ShouldNotHaveValidationErrorFor(c => c.CallerId);
    }

    [Fact]
    public void Validate_CallerIdAtMaxLength_Passes()
    {
        var command = new RefreshSkillRegistryCommand
        {
            CallerId = new string('a', SkillRegistryValidationRules.MaxCallerIdLength),
        };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(c => c.CallerId);
    }
}
