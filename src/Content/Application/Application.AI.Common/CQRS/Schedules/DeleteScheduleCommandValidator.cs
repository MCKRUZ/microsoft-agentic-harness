using FluentValidation;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Validates <see cref="DeleteScheduleCommand"/>.</summary>
public sealed class DeleteScheduleCommandValidator : AbstractValidator<DeleteScheduleCommand>
{
    public DeleteScheduleCommandValidator()
    {
        RuleFor(x => x.ScheduleId).NotEmpty().WithMessage("ScheduleId must not be empty.");
        RuleFor(x => x.OwnerId).NotEmpty().WithMessage("OwnerId must not be empty.");
    }
}
