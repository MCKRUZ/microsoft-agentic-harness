using FluentValidation;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Validates <see cref="GeneratePlanCommand"/>: task description must not be empty.
/// </summary>
public sealed class GeneratePlanCommandValidator : AbstractValidator<GeneratePlanCommand>
{
    public GeneratePlanCommandValidator()
    {
        RuleFor(x => x.TaskDescription)
            .NotEmpty().WithMessage("Task description must not be empty.")
            .MaximumLength(10_000).WithMessage("Task description must not exceed 10000 characters.");
    }
}
