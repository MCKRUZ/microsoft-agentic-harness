using FluentValidation;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Validates <see cref="RecalculateDriftBaselineCommand"/>: a non-empty baseline id and a
/// present, bounded controller-stamped caller identity.
/// </summary>
public sealed class RecalculateDriftBaselineCommandValidator
    : AbstractValidator<RecalculateDriftBaselineCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public RecalculateDriftBaselineCommandValidator()
    {
        RuleFor(x => x.BaselineId)
            .NotEmpty().WithMessage("BaselineId must not be empty.");

        RuleFor(x => x.CallerId)
            .NotEmpty().WithMessage("CallerId must not be empty.")
            .MaximumLength(DriftValidationRules.MaxCallerIdLength)
                .WithMessage($"CallerId must not exceed {DriftValidationRules.MaxCallerIdLength} characters.");
    }
}
