using FluentValidation;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Validates <see cref="ImproveLearningCommand"/>: LearningId not empty, FeedbackScore in [1.0, 5.0].
/// </summary>
public sealed class ImproveLearningCommandValidator : AbstractValidator<ImproveLearningCommand>
{
    public ImproveLearningCommandValidator()
    {
        RuleFor(x => x.LearningId)
            .NotEqual(Guid.Empty).WithMessage("LearningId must not be empty.");

        RuleFor(x => x.FeedbackScore)
            .InclusiveBetween(1.0, 5.0).WithMessage("FeedbackScore must be between 1.0 and 5.0.");
    }
}
