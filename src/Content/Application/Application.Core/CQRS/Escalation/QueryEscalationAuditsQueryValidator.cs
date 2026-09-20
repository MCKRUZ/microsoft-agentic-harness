using Domain.AI.Escalation;
using Domain.Common.Constants;
using FluentValidation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Validates <see cref="QueryEscalationAuditsQuery"/>: an ordered window when both ends are
/// supplied, a defined record-type filter, and a bounded result cap.
/// </summary>
public sealed class QueryEscalationAuditsQueryValidator : AbstractValidator<QueryEscalationAuditsQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public QueryEscalationAuditsQueryValidator()
    {
        RuleFor(x => x.Start)
            .LessThan(x => x.End)
            .When(x => x.Start.HasValue && x.End.HasValue)
            .WithMessage("Start must be before End.");

        RuleFor(x => x.RecordType)
            .Must(recordType => recordType is null || Enum.IsDefined(recordType.Value))
            .WithMessage("RecordType must be a defined EscalationAuditRecordType value.");

        RuleFor(x => x.MaxResults)
            .InclusiveBetween(1, AuditQueryDefaults.MaxResults)
            .WithMessage($"MaxResults must be between 1 and {AuditQueryDefaults.MaxResults}.");
    }
}
