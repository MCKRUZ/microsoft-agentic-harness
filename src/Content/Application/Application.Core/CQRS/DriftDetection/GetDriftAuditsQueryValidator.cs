using Domain.AI.DriftDetection;
using FluentValidation;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Validates <see cref="GetDriftAuditsQuery"/>: an ordered window when both ends are supplied,
/// a defined record-type filter, and a bounded result cap.
/// </summary>
public sealed class GetDriftAuditsQueryValidator : AbstractValidator<GetDriftAuditsQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetDriftAuditsQueryValidator()
    {
        RuleFor(x => x.Start)
            .LessThan(x => x.End)
            .When(x => x.Start.HasValue && x.End.HasValue)
            .WithMessage("Start must be before End.");

        RuleFor(x => x.RecordType)
            .Must(recordType => recordType is null || Enum.IsDefined(recordType.Value))
            .WithMessage("RecordType must be a defined DriftAuditRecordType value.");

        RuleFor(x => x.MaxResults)
            .InclusiveBetween(1, DriftValidationRules.MaxAuditResults)
            .WithMessage($"MaxResults must be between 1 and {DriftValidationRules.MaxAuditResults}.");
    }
}
