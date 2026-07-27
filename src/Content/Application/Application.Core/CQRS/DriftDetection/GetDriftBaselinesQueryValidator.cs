using Domain.AI.DriftDetection;
using FluentValidation;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Validates <see cref="GetDriftBaselinesQuery"/>: the optional scope filter, when present,
/// must be a defined <see cref="DriftScope"/> member (model binding accepts any integer).
/// </summary>
public sealed class GetDriftBaselinesQueryValidator : AbstractValidator<GetDriftBaselinesQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetDriftBaselinesQueryValidator()
    {
        RuleFor(x => x.Scope)
            .Must(scope => scope is null || Enum.IsDefined(scope.Value))
            .WithMessage("Scope must be a defined DriftScope value.");
    }
}
