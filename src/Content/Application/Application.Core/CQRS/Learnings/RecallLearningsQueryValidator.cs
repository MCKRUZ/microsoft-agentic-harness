using FluentValidation;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Validates <see cref="RecallLearningsQuery"/>: context non-empty and bounded, MaxResults within
/// [1, <see cref="LearningsValidationRules.MaxRecallResults"/>]. Mirrors the memory surface's
/// <c>RecallMemoryQueryValidator</c> so both HTTP recall surfaces enforce the same style of
/// wire-level bounds.
/// </summary>
public sealed class RecallLearningsQueryValidator : AbstractValidator<RecallLearningsQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public RecallLearningsQueryValidator()
    {
        RuleFor(x => x.Context)
            .NotEmpty().WithMessage("Context must not be empty.")
            .MaximumLength(LearningsValidationRules.MaxContextLength)
                .WithMessage($"Context must not exceed {LearningsValidationRules.MaxContextLength} characters.");

        RuleFor(x => x.MaxResults)
            .InclusiveBetween(1, LearningsValidationRules.MaxRecallResults)
                .WithMessage($"MaxResults must be between 1 and {LearningsValidationRules.MaxRecallResults}.");
    }
}
