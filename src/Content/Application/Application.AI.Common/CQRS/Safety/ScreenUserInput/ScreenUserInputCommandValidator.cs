using FluentValidation;

namespace Application.AI.Common.CQRS.Safety.ScreenUserInput;

/// <summary>
/// Validates <see cref="ScreenUserInputCommand"/>: there must be text to screen.
/// </summary>
public sealed class ScreenUserInputCommandValidator : AbstractValidator<ScreenUserInputCommand>
{
    /// <summary>Initializes the validator.</summary>
    public ScreenUserInputCommandValidator()
    {
        RuleFor(x => x.Input)
            .NotEmpty().WithMessage("Input is required: there is nothing to screen.");
    }
}
