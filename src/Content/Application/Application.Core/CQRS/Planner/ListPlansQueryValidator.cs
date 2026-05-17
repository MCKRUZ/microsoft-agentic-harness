using FluentValidation;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Validates <see cref="ListPlansQuery"/>: To must not be before From when both are specified.
/// </summary>
public sealed class ListPlansQueryValidator : AbstractValidator<ListPlansQuery>
{
    public ListPlansQueryValidator()
    {
        RuleFor(x => x.To)
            .Must((query, to) => to >= query.From)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage("'To' date must not be before 'From' date.");
    }
}
