using FluentValidation;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Validates <see cref="DriftHistoryQuery"/> ensuring scope identifier is provided
/// and the time window is ordered correctly.
/// </summary>
public sealed class DriftHistoryQueryValidator : AbstractValidator<DriftHistoryQuery>
{
    public DriftHistoryQueryValidator()
    {
        RuleFor(x => x.ScopeIdentifier)
            .NotEmpty().WithMessage("ScopeIdentifier must not be empty.");

        RuleFor(x => x.Start)
            .LessThan(x => x.End).WithMessage("Start must be before End.");
    }
}
