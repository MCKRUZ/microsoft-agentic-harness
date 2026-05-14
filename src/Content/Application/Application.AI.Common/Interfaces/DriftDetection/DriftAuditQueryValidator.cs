using FluentValidation;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Validates <see cref="DriftAuditQuery"/> ensuring Start is before End when both are provided.
/// </summary>
public sealed class DriftAuditQueryValidator : AbstractValidator<DriftAuditQuery>
{
    public DriftAuditQueryValidator()
    {
        RuleFor(x => x.Start)
            .LessThan(x => x.End)
            .When(x => x.Start.HasValue && x.End.HasValue)
            .WithMessage("Start must be before End.");
    }
}
