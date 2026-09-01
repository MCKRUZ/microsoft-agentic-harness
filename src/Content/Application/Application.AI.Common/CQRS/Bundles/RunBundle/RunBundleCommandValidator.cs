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

    /// <summary>
    /// Upper bound on a conversation id's length. Ids are caller-supplied and opaque; this stops an
    /// unbounded one reaching a store that makes it a file name or a primary key.
    /// </summary>
    public const int MaxConversationIdLength = 200;

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

        // Applied only when a conversation is named, so a one-shot run is unaffected.
        //
        // The charset is deliberately narrow, and it is a real control rather than tidiness: the
        // file-backed store turns an id into a file name and rejects one that escapes its directory,
        // while the SQLite store accepts whatever it is handed. Validating here means a traversal
        // attempt is refused identically whichever provider a consumer has configured, instead of
        // depending on a rejection the store interface explicitly tells callers not to rely on.
        When(x => x.ConversationId is not null, () =>
        {
            RuleFor(x => x.ConversationId)
                .NotEmpty().WithMessage("ConversationId must not be blank when supplied.")
                .MaximumLength(MaxConversationIdLength)
                    .WithMessage($"ConversationId exceeds {MaxConversationIdLength} characters.")
                // #576: \A/\z, not ^/$ — $ in .NET regex matches immediately before a trailing '\n' as
                // well as at the true end of the string, so a conversation id ending in a newline would
                // otherwise pass this charset check (the same bug class already fixed in
                // StorageSegmentSafety.AllowedCharset — see that type's own remarks).
                .Matches(@"\A[A-Za-z0-9_-]+\z")
                    .WithMessage(
                        "ConversationId may contain only letters, digits, hyphens and underscores.");
        });
    }
}
