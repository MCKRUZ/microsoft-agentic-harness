using FluentValidation;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Validates <see cref="ResumeScheduleCommand"/>.</summary>
public sealed class ResumeScheduleCommandValidator : AbstractValidator<ResumeScheduleCommand>
{
    public ResumeScheduleCommandValidator()
    {
        RuleFor(x => x.ScheduleId).NotEmpty().WithMessage("ScheduleId must not be empty.");
        RuleFor(x => x.OwnerId).NotEmpty().WithMessage("OwnerId must not be empty.");
    }
}
