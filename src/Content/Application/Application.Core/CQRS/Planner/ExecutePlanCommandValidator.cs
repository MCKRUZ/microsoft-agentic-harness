using FluentValidation;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Validates <see cref="ExecutePlanCommand"/>: PlanId must not wrap Guid.Empty.
/// </summary>
public sealed class ExecutePlanCommandValidator : AbstractValidator<ExecutePlanCommand>
{
    public ExecutePlanCommandValidator()
    {
        RuleFor(x => x.PlanId)
            .Must(id => id.Value != Guid.Empty)
            .WithMessage("PlanId must not be empty.");
    }
}
