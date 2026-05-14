using FluentValidation;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Validates <see cref="DriftEvaluationRequest"/> ensuring scope identifier and dimensions are provided.
/// </summary>
public sealed class DriftEvaluationRequestValidator : AbstractValidator<DriftEvaluationRequest>
{
    public DriftEvaluationRequestValidator()
    {
        RuleFor(x => x.ScopeIdentifier)
            .NotEmpty().WithMessage("ScopeIdentifier must not be empty.");

        RuleFor(x => x.Dimensions)
            .NotEmpty().WithMessage("At least one dimension must be provided.");
    }
}
