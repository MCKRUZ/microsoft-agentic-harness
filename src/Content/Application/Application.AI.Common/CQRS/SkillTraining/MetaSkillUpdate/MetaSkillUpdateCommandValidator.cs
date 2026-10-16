using FluentValidation;

namespace Application.AI.Common.CQRS.SkillTraining.MetaSkillUpdate;

/// <summary>Validates <see cref="MetaSkillUpdateCommand"/>.</summary>
public sealed class MetaSkillUpdateCommandValidator : AbstractValidator<MetaSkillUpdateCommand>
{
    /// <summary>Initializes the validator.</summary>
    public MetaSkillUpdateCommandValidator()
    {
        RuleFor(x => x.RunId).NotEmpty();
        RuleFor(x => x.SkillId).NotEmpty();
        RuleFor(x => x.Domain).NotEmpty();
        RuleFor(x => x.Epoch).GreaterThanOrEqualTo(1);
        RuleFor(x => x.CurrentSkill).NotEmpty();
    }
}
