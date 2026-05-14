using Domain.Common.Config.AI.DriftDetection;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="DriftDetectionConfig"/> constraints including
/// EWMA parameter ranges and threshold ordering invariant (Warn &lt; Alert &lt; Escalate).
/// </summary>
public sealed class DriftDetectionConfigValidator : AbstractValidator<DriftDetectionConfig>
{
    public DriftDetectionConfigValidator()
    {
        RuleFor(x => x.EwmaLambda)
            .GreaterThan(0).WithMessage("EwmaLambda must be > 0.")
            .LessThanOrEqualTo(1).WithMessage("EwmaLambda must be <= 1.");

        RuleFor(x => x.ControlLimitWidth)
            .GreaterThan(0).WithMessage("ControlLimitWidth must be > 0.");

        RuleFor(x => x.MinSamplesForBaseline)
            .GreaterThan(0).WithMessage("MinSamplesForBaseline must be > 0.");

        RuleFor(x => x.BaselineWindowDays)
            .GreaterThan(0).WithMessage("BaselineWindowDays must be > 0.");

        RuleFor(x => x.WarnThresholdSigma)
            .GreaterThan(0).WithMessage("WarnThresholdSigma must be > 0.")
            .LessThan(x => x.AlertThresholdSigma)
            .WithMessage("WarnThresholdSigma must be less than AlertThresholdSigma.");

        RuleFor(x => x.AlertThresholdSigma)
            .GreaterThan(0).WithMessage("AlertThresholdSigma must be > 0.")
            .LessThan(x => x.EscalateThresholdSigma)
            .WithMessage("AlertThresholdSigma must be less than EscalateThresholdSigma.");

        RuleFor(x => x.EscalateThresholdSigma)
            .GreaterThan(0).WithMessage("EscalateThresholdSigma must be > 0.");

        RuleFor(x => x.AuditPath)
            .NotEmpty().WithMessage("AuditPath must be configured.");
    }
}
