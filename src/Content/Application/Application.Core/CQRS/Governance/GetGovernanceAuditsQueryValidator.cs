using Domain.Common.Constants;
using FluentValidation;

namespace Application.Core.CQRS.Governance;

/// <summary>
/// Validates <see cref="GetGovernanceAuditsQuery"/>: an ordered window when both ends are
/// supplied, and a bounded result cap.
/// </summary>
public sealed class GetGovernanceAuditsQueryValidator : AbstractValidator<GetGovernanceAuditsQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetGovernanceAuditsQueryValidator()
    {
        RuleFor(x => x.Start)
            .LessThan(x => x.End)
            .When(x => x.Start.HasValue && x.End.HasValue)
            .WithMessage("Start must be before End.");

        RuleFor(x => x.MaxResults)
            .InclusiveBetween(1, AuditQueryDefaults.MaxResults)
            .WithMessage($"MaxResults must be between 1 and {AuditQueryDefaults.MaxResults}.");
    }
}
