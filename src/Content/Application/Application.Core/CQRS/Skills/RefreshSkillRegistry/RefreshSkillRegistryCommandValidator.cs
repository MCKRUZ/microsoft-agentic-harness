using FluentValidation;

namespace Application.Core.CQRS.Skills.RefreshSkillRegistry;

/// <summary>Validates <see cref="RefreshSkillRegistryCommand"/>: a present, bounded controller-stamped caller identity.</summary>
public sealed class RefreshSkillRegistryCommandValidator : AbstractValidator<RefreshSkillRegistryCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public RefreshSkillRegistryCommandValidator()
    {
        RuleFor(x => x.CallerId)
            .NotEmpty().WithMessage("CallerId must not be empty.")
            .MaximumLength(SkillRegistryValidationRules.MaxCallerIdLength)
                .WithMessage($"CallerId must not exceed {SkillRegistryValidationRules.MaxCallerIdLength} characters.");
    }
}
