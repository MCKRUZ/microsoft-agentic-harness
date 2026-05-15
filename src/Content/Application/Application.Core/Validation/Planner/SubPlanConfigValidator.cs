using Domain.AI.Planner;
using FluentValidation;

namespace Application.Core.Validation.Planner;

/// <summary>
/// Validates <see cref="SubPlanConfig"/> constraints. Exactly one of
/// <see cref="SubPlanConfig.ChildPlanId"/> or <see cref="SubPlanConfig.InlinePlanDefinition"/>
/// must be provided.
/// </summary>
public sealed class SubPlanConfigValidator : AbstractValidator<SubPlanConfig>
{
    public SubPlanConfigValidator()
    {
        RuleFor(x => x)
            .Must(x => x.ChildPlanId.HasValue ^ (x.InlinePlanDefinition is not null))
            .WithMessage("Exactly one of ChildPlanId or InlinePlanDefinition must be provided (not both).");
    }
}
