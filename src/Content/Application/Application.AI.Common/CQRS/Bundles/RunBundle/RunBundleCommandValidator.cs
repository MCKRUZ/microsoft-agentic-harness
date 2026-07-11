using FluentValidation;

namespace Application.AI.Common.CQRS.Bundles.RunBundle;

/// <summary>
/// Validates <see cref="RunBundleCommand"/> before the handler runs.
/// </summary>
public sealed class RunBundleCommandValidator : AbstractValidator<RunBundleCommand>
{
    /// <summary>Upper bound on seed messages — a run seeds at most this many turns.</summary>
    public const int MaxUserMessages = 100;

    /// <summary>Upper bound on turns — a runaway turn count is almost always a caller bug.</summary>
    public const int MaxTurnsLimit = 100;

    /// <summary>Initializes validation rules.</summary>
    public RunBundleCommandValidator()
    {
        RuleFor(x => x.Handle)
            .NotEmpty().WithMessage("Handle is required.");

        RuleFor(x => x.UserMessages)
            .NotNull().WithMessage("UserMessages is required.")
            .Must(m => m is null || m.Count > 0).WithMessage("UserMessages must contain at least one message.")
            .Must(m => m is null || m.Count <= MaxUserMessages)
                .WithMessage($"UserMessages exceeds {MaxUserMessages} messages.");

        RuleForEach(x => x.UserMessages)
            .NotEmpty().WithMessage("User messages must be non-empty.");

        RuleFor(x => x.Envelope)
            .NotNull().WithMessage("Envelope is required.");

        RuleFor(x => x.MaxTurns)
            .GreaterThan(0).WithMessage("MaxTurns must be greater than zero.")
            .LessThanOrEqualTo(MaxTurnsLimit).WithMessage($"MaxTurns exceeds {MaxTurnsLimit}.");
    }
}
