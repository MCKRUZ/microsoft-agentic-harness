using Domain.Common.Config.AI.Schedules;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ScheduleConfig"/>. Every rule is unconditional (not gated on
/// <see cref="ScheduleConfig.Enabled"/>) — matching <c>WorkflowSubmissionConfigValidator</c>'s
/// convention, for the same reason: the class defaults are valid, so a rule only bites when an
/// operator supplies an explicit bad value.
/// </summary>
public sealed class ScheduleConfigValidator : AbstractValidator<ScheduleConfig>
{
    public ScheduleConfigValidator()
    {
        RuleFor(x => x.TickInterval)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("TickInterval must be > 0 — a non-positive interval would spin the tick service continuously instead of scheduling it.");

        RuleFor(x => x.MaxSchedulesPerOwner)
            .GreaterThan(0)
            .WithMessage("MaxSchedulesPerOwner must be > 0 — a non-positive quota would refuse every caller's first schedule.");
    }
}
