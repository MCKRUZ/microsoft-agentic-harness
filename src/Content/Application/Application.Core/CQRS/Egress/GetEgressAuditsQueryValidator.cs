using Domain.Common.Constants;
using FluentValidation;

namespace Application.Core.CQRS.Egress;

/// <summary>
/// Validates <see cref="GetEgressAuditsQuery"/>: an ordered window when both ends are supplied,
/// and a bounded result cap.
/// </summary>
public sealed class GetEgressAuditsQueryValidator : AbstractValidator<GetEgressAuditsQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetEgressAuditsQueryValidator()
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
