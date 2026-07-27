using FluentValidation;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// Validates <see cref="RecallMemoryQuery"/>: query non-empty and bounded, MaxResults within
/// [1, <see cref="MemoryValidationRules.MaxRecallResults"/>].
/// </summary>
public sealed class RecallMemoryQueryValidator : AbstractValidator<RecallMemoryQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public RecallMemoryQueryValidator()
    {
        RuleFor(x => x.Query)
            .NotEmpty().WithMessage("Query must not be empty.")
            .MaximumLength(MemoryValidationRules.MaxQueryLength)
                .WithMessage($"Query must not exceed {MemoryValidationRules.MaxQueryLength} characters.");

        RuleFor(x => x.MaxResults)
            .InclusiveBetween(1, MemoryValidationRules.MaxRecallResults)
                .WithMessage($"MaxResults must be between 1 and {MemoryValidationRules.MaxRecallResults}.");
    }
}
