using FluentValidation;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Validates <see cref="GetPlanHistoryQuery"/>: PlanId must not wrap Guid.Empty.
/// </summary>
public sealed class GetPlanHistoryQueryValidator : AbstractValidator<GetPlanHistoryQuery>
{
    public GetPlanHistoryQueryValidator()
    {
        RuleFor(x => x.PlanId)
            .Must(id => id.Value != Guid.Empty)
            .WithMessage("PlanId must not be empty.");
    }
}
