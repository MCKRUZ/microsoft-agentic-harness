using Domain.Common.Config.AI.Learnings;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="LearningsConfig"/> constraints including feedback blending ranges,
/// diversity ratio bounds, shelf life positivity, and baseline adjustment threshold.
/// </summary>
public sealed class LearningsConfigValidator : AbstractValidator<LearningsConfig>
{
    public LearningsConfigValidator()
    {
        RuleFor(x => x.FeedbackAlpha)
            .GreaterThan(0).WithMessage("FeedbackAlpha must be > 0.")
            .LessThanOrEqualTo(1).WithMessage("FeedbackAlpha must be <= 1.");

        RuleFor(x => x.FeedbackCeiling)
            .GreaterThan(0).WithMessage("FeedbackCeiling must be > 0.")
            .LessThanOrEqualTo(1).WithMessage("FeedbackCeiling must be <= 1.");

        RuleFor(x => x.DiversityInjectionRatio)
            .GreaterThanOrEqualTo(0).WithMessage("DiversityInjectionRatio must be >= 0.")
            .LessThanOrEqualTo(0.5).WithMessage("DiversityInjectionRatio must be <= 0.5.");

        RuleFor(x => x.VolatileShelfLifeDays)
            .GreaterThan(0).WithMessage("VolatileShelfLifeDays must be > 0.");

        RuleFor(x => x.StableShelfLifeDays)
            .GreaterThan(0).WithMessage("StableShelfLifeDays must be > 0.");

        RuleFor(x => x.PruneIntervalHours)
            .GreaterThan(0).WithMessage("PruneIntervalHours must be > 0.");

        RuleFor(x => x.BaselineAdjustmentThreshold)
            .GreaterThan(0).WithMessage("BaselineAdjustmentThreshold must be > 0.")
            .LessThanOrEqualTo(1).WithMessage("BaselineAdjustmentThreshold must be <= 1.");

        RuleFor(x => x.DecayBiasAlpha)
            .GreaterThan(0).WithMessage("DecayBiasAlpha must be > 0.")
            .LessThanOrEqualTo(1).WithMessage("DecayBiasAlpha must be <= 1.");

        RuleFor(x => x.StoreProvider)
            .NotEmpty().WithMessage("StoreProvider must be configured (e.g., 'graph' or 'in_memory').");
    }
}
