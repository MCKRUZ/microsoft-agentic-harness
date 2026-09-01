using Domain.Common.Helpers;
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
    /// Upper bound on a conversation id's length, kept only for tests to assert against by name — the
    /// actual rule is enforced by <see cref="StorageSegmentSafety.AllowedCharset"/>'s own embedded
    /// <c>{1,200}</c> bound, not by a separate length rule in this validator. Must match that bound; it
    /// is not derived from it because <see cref="StorageSegmentSafety"/> does not expose its length
    /// separately from its charset.
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
        //
        // Reuse finding (/code-review, #576): this id becomes a tool-result storage scope on the same
        // path RunConversationCommandValidator/RunOrchestratedTaskCommandValidator already guard with
        // Domain.Common.Helpers.StorageSegmentSafety — see that type's own remarks for the full history
        // of this exact charset-plus-shape check being hand-copied (and drifting) across prior call
        // sites before extraction. Routed through the same shared checks here rather than a fresh,
        // narrower inline regex that both lacked the "."/".."/trailing-dot/rooted-path guards the
        // shared helper provides and needed its own independent #576 anchor fix. MaximumLength is
        // dropped in favor of AllowedCharset's own built-in {1,200} bound, matching those siblings.
        When(x => x.ConversationId is not null, () =>
        {
            RuleFor(x => x.ConversationId)
                .Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("ConversationId must not be blank when supplied.")
                .Matches(StorageSegmentSafety.AllowedCharset)
                    .WithMessage("ConversationId must be 1-200 characters from [A-Za-z0-9_.:-].")
                .Must(id => !StorageSegmentSafety.IsRelativeDirectoryReference(id))
                    .WithMessage("ConversationId must not be a relative directory reference.")
                .Must(id => !Path.IsPathRooted(id))
                    .WithMessage("ConversationId must not be an absolute or drive-rooted path.")
                .Must(id => !StorageSegmentSafety.HasTrailingDot(id))
                    .WithMessage("ConversationId must not have a trailing dot.");
        });
    }
}
