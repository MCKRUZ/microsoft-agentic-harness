using FluentValidation;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Validates <see cref="PauseScheduleCommand"/>.</summary>
public sealed class PauseScheduleCommandValidator : AbstractValidator<PauseScheduleCommand>
{
    public PauseScheduleCommandValidator()
    {
        RuleFor(x => x.ScheduleId).NotEmpty().WithMessage("ScheduleId must not be empty.");
        RuleFor(x => x.OwnerId).NotEmpty().WithMessage("OwnerId must not be empty.");
    }
}
