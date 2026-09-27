using FluentValidation;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Validates <see cref="ListSchedulesQuery"/>.</summary>
public sealed class ListSchedulesQueryValidator : AbstractValidator<ListSchedulesQuery>
{
    public ListSchedulesQueryValidator()
    {
        RuleFor(x => x.OwnerId).NotEmpty().WithMessage("OwnerId must not be empty.");
    }
}
