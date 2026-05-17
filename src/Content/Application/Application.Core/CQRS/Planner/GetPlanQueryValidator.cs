using FluentValidation;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Validates <see cref="GetPlanQuery"/>: PlanId must not wrap Guid.Empty.
/// </summary>
public sealed class GetPlanQueryValidator : AbstractValidator<GetPlanQuery>
{
    public GetPlanQueryValidator()
    {
        RuleFor(x => x.PlanId)
            .Must(id => id.Value != Guid.Empty)
            .WithMessage("PlanId must not be empty.");
    }
}
