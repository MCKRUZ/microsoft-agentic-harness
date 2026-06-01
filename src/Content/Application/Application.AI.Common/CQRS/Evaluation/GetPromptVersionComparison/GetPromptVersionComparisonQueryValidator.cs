using FluentValidation;

namespace Application.AI.Common.CQRS.Evaluation.GetPromptVersionComparison;

/// <summary>Validates <see cref="GetPromptVersionComparisonQuery"/>.</summary>
public sealed class GetPromptVersionComparisonQueryValidator
    : AbstractValidator<GetPromptVersionComparisonQuery>
{
    /// <summary>Initializes the validator.</summary>
    public GetPromptVersionComparisonQueryValidator()
    {
        RuleFor(x => x.PromptName).NotEmpty().WithMessage("PromptName is required.");
    }
}
