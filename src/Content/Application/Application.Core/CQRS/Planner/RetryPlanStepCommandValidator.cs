using FluentValidation;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Validates <see cref="RetryPlanStepCommand"/>: both PlanId and StepId must not be empty.
/// </summary>
public sealed class RetryPlanStepCommandValidator : AbstractValidator<RetryPlanStepCommand>
{
    public RetryPlanStepCommandValidator()
    {
        RuleFor(x => x.PlanId)
            .Must(id => id.Value != Guid.Empty)
            .WithMessage("PlanId must not be empty.");

        RuleFor(x => x.StepId)
            .Must(id => id.Value != Guid.Empty)
            .WithMessage("StepId must not be empty.");
    }
}
