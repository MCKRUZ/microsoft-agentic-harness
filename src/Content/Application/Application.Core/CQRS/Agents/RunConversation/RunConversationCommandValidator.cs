using Domain.Common.Helpers;
using FluentValidation;

namespace Application.Core.CQRS.Agents.RunConversation;

/// <summary>
/// Validates <see cref="RunConversationCommand"/> before execution.
/// </summary>
public class RunConversationCommandValidator : AbstractValidator<RunConversationCommand>
{
	public RunConversationCommandValidator()
	{
		RuleFor(x => x.AgentName)
			.NotEmpty().WithMessage("Agent name is required.");

		RuleFor(x => x.UserMessages)
			.NotEmpty().WithMessage("At least one user message is required.");

		RuleFor(x => x.MaxTurns)
			.InclusiveBetween(1, 100).WithMessage("Max turns must be between 1 and 100.");

		// Applied only when the property is present, so omitting it stays the way to run a
		// self-contained conversation. Supplying it blank is rejected outright rather than read as
		// omission: a blank identity that flows onward widens access instead of narrowing it.
		RuleFor(x => x.ConversationOwnerId)
			.NotEmpty().WithMessage("Conversation owner id must not be blank when supplied.")
			.When(x => x.ConversationOwnerId is not null);

		// Unconditional, not gated on ConversationOwnerId being present (#560): this id becomes a
		// tool-result storage scope on every path, owner or not, so a caller-supplied value with a
		// path separator or other unsafe character must be rejected here rather than reaching
		// FileSystemToolResultStore as a late, less legible ArgumentException. The command's own
		// default (a bare GUID) always satisfies this.
		// StorageSegmentSafety (Domain.Common.Helpers) is the single shared home for this charset and
		// its independent rooted-path / "." / ".." / trailing-dot checks — see its own remarks for why
		// each of those matters beyond the charset alone, and for the history of this exact check being
		// hand-copied (and drifting) across four call sites before being extracted here.
		// /code-review finding: Cascade(Stop) so a null ConversationId — CLR nullable-reference
		// annotations do not stop a lenient JSON deserializer from setting a non-nullable string
		// property to null at runtime — gets exactly the one clear "required" error instead of
		// FluentValidation's default Continue cascade running every subsequent rule (Matches, Must)
		// against that same null value. StorageSegmentSafety's own checks are null-safe as a second,
		// independent layer, but this is the correct place to stop: nothing past NotEmpty is
		// meaningful to evaluate on a value that already failed it.
		RuleFor(x => x.ConversationId)
			.Cascade(CascadeMode.Stop)
			.NotEmpty().WithMessage("Conversation id is required for a durable conversation.")
			.Matches(StorageSegmentSafety.AllowedCharset)
				.WithMessage("Conversation id must be 1-200 characters from [A-Za-z0-9_.:-].")
			.Must(id => !StorageSegmentSafety.IsRelativeDirectoryReference(id))
				.WithMessage("Conversation id must not be a relative directory reference.")
			.Must(id => !Path.IsPathRooted(id))
				.WithMessage("Conversation id must not be an absolute or drive-rooted path.")
			.Must(id => !StorageSegmentSafety.HasTrailingDot(id))
				.WithMessage("Conversation id must not have a trailing dot.");
	}
}
