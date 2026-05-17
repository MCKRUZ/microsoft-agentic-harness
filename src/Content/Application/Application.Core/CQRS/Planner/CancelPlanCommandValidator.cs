using FluentValidation;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Validates <see cref="CancelPlanCommand"/>: PlanId must not wrap Guid.Empty.
/// </summary>
public sealed class CancelPlanCommandValidator : AbstractValidator<CancelPlanCommand>
{
    public CancelPlanCommandValidator()
    {
        RuleFor(x => x.PlanId)
            .Must(id => id.Value != Guid.Empty)
            .WithMessage("PlanId must not be empty.");
    }
}
