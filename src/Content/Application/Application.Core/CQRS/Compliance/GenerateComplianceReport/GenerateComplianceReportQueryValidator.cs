using Domain.Common.Constants;
using FluentValidation;

namespace Application.Core.CQRS.Compliance.GenerateComplianceReport;

/// <summary>
/// Validates <see cref="GenerateComplianceReportQuery"/>: an ordered, bounded-length window, a
/// non-blank caller id, and a bounded per-source record cap.
/// </summary>
public sealed class GenerateComplianceReportQueryValidator : AbstractValidator<GenerateComplianceReportQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GenerateComplianceReportQueryValidator()
    {
        RuleFor(x => x.CallerId)
            .NotEmpty();

        RuleFor(x => x.Start)
            .LessThanOrEqualTo(x => x.End)
            .WithMessage("Start must not be after End.");

        RuleFor(x => x)
            .Must(x => (x.End - x.Start).TotalDays <= ComplianceReportDefaults.MaxWindowDays)
            .WithMessage($"The reporting window must not exceed {ComplianceReportDefaults.MaxWindowDays} days.")
            .WithName(nameof(GenerateComplianceReportQuery.End));

        RuleFor(x => x.MaxRecordsPerSource)
            .InclusiveBetween(1, AuditQueryDefaults.MaxResults)
            .WithMessage($"MaxRecordsPerSource must be between 1 and {AuditQueryDefaults.MaxResults}.");
    }
}
