using FluentValidation;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Validates <see cref="DriftBaselineUpdateRequest"/> ensuring scope identifier is provided.
/// </summary>
public sealed class DriftBaselineUpdateRequestValidator : AbstractValidator<DriftBaselineUpdateRequest>
{
    public DriftBaselineUpdateRequestValidator()
    {
        RuleFor(x => x.ScopeIdentifier)
            .NotEmpty().WithMessage("ScopeIdentifier must not be empty.");
    }
}
