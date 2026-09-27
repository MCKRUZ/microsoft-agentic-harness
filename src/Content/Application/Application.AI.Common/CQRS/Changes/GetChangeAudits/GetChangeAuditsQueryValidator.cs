using Domain.AI.Changes;
using Domain.Common.Constants;
using FluentValidation;

namespace Application.AI.Common.CQRS.Changes.GetChangeAudits;

/// <summary>
/// Validates <see cref="GetChangeAuditsQuery"/>: an ordered window when both ends are supplied,
/// a defined decision filter, and a bounded result cap.
/// </summary>
public sealed class GetChangeAuditsQueryValidator : AbstractValidator<GetChangeAuditsQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetChangeAuditsQueryValidator()
    {
        RuleFor(x => x.Start)
            .LessThan(x => x.End)
            .When(x => x.Start.HasValue && x.End.HasValue)
            .WithMessage("Start must be before End.");

        RuleFor(x => x.Decision)
            .Must(decision => decision is null || Enum.IsDefined(decision.Value))
            .WithMessage("Decision must be a defined GateAction value.");

        RuleFor(x => x.MaxResults)
            .InclusiveBetween(1, AuditQueryDefaults.MaxResults)
            .WithMessage($"MaxResults must be between 1 and {AuditQueryDefaults.MaxResults}.");
    }
}
