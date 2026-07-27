using Domain.AI.DriftDetection;
using FluentValidation;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Validates <see cref="PushDriftEvaluationCommand"/>. The score-range rules are the poison
/// guard: pushed values feed EWMA state and future baselines, so out-of-range or non-finite
/// values (NaN, ±Infinity) — which would silently corrupt every downstream mean and sigma —
/// are rejected at the boundary.
/// </summary>
public sealed class PushDriftEvaluationCommandValidator
    : AbstractValidator<PushDriftEvaluationCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public PushDriftEvaluationCommandValidator()
    {
        RuleFor(x => x.Scope)
            .Must(scope => Enum.IsDefined(scope))
            .WithMessage("Scope must be a defined DriftScope value.");

        RuleFor(x => x.ScopeIdentifier)
            .NotEmpty().WithMessage("ScopeIdentifier must not be empty.")
            .MaximumLength(DriftValidationRules.MaxScopeIdentifierLength)
                .WithMessage($"ScopeIdentifier must not exceed {DriftValidationRules.MaxScopeIdentifierLength} characters.");

        RuleFor(x => x.CallerId)
            .NotEmpty().WithMessage("CallerId must not be empty.")
            .MaximumLength(DriftValidationRules.MaxCallerIdLength)
                .WithMessage($"CallerId must not exceed {DriftValidationRules.MaxCallerIdLength} characters.");

        RuleFor(x => x.Dimensions)
            .NotEmpty().WithMessage("At least one dimension must be provided.")
            .Must(dimensions => dimensions is null || dimensions.Count <= DriftValidationRules.MaxDimensionsPerEvaluation)
                .WithMessage($"At most {DriftValidationRules.MaxDimensionsPerEvaluation} dimensions may be provided.");

        RuleForEach(x => x.Dimensions)
            .Must(entry => Enum.IsDefined(entry.Key))
                .WithMessage("Every dimension key must be a defined DriftDimension value.")
            .Must(entry => double.IsFinite(entry.Value) && entry.Value is >= 0.0 and <= 1.0)
                .WithMessage("Every dimension score must be a finite value in [0, 1].");
    }
}
