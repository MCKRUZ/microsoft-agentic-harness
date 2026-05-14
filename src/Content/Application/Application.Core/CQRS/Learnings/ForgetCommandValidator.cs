using FluentValidation;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Validates <see cref="ForgetCommand"/>: LearningId not empty, Reason not empty.
/// </summary>
public sealed class ForgetCommandValidator : AbstractValidator<ForgetCommand>
{
    public ForgetCommandValidator()
    {
        RuleFor(x => x.LearningId)
            .NotEqual(Guid.Empty).WithMessage("LearningId must not be empty.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason must not be empty.");
    }
}
