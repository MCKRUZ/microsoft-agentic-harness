using FluentValidation;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Validates <see cref="CreatePlanCommand"/>: plan graph must not be null.
/// </summary>
public sealed class CreatePlanCommandValidator : AbstractValidator<CreatePlanCommand>
{
    public CreatePlanCommandValidator()
    {
        RuleFor(x => x.Plan)
            .NotNull().WithMessage("Plan graph must not be null.");
    }
}
