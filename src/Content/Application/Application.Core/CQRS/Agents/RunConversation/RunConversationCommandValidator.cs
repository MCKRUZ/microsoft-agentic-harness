using System.Text.RegularExpressions;
using FluentValidation;

namespace Application.Core.CQRS.Agents.RunConversation;

/// <summary>
/// Validates <see cref="RunConversationCommand"/> before execution.
/// </summary>
public class RunConversationCommandValidator : AbstractValidator<RunConversationCommand>
{
	// #560: ConversationId becomes a directory name via IAgentExecutionContext.ToolResultScopeId ->
	// FileSystemToolResultStore. Must match that store's own allowlist exactly — kept in sync by
	// comment rather than a shared constant because the store lives in Infrastructure and this
	// validator in Application; Application cannot reference Infrastructure. If the store's charset
	// changes, this must change with it.
	// ':' IS admitted — PlanRunKeys.StepConversationId builds every plan-step conversation id as
	// "{runScope}:{stepId}", so excluding it here rejects every LLM step of every plan run (a real
	// regression this branch shipped once already). Path.IsPathRooted still measures a bare drive
	// reference like "C:" as rooted on Windows; that check lives in the store, not this charset — see
	// FileSystemToolResultStore.AllowedSegmentCharset's remarks for why the charset does not need to
	// encode that distinction itself.
	// Length bound is 200, not 128 — a second real regression on this same rule: 128 matched
	// IPlanRunExecutor.MaxAgentIdLength (the cap on a bare run scope), but the derived
	// "{runScope}:{stepId}" shape above can reach 128 + 1 + 36 = 165 characters, which the OLD
	// 128-char bound rejected outright. See FileSystemToolResultStore.AllowedSegmentCharset's remarks
	// for the exact arithmetic. Anchored with \A/\z, not ^/$ — $ matches before a trailing '\n' too.
	private static readonly Regex AllowedScopeIdCharset = new(@"\A[A-Za-z0-9_.:-]{1,200}\z", RegexOptions.Compiled);

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
		// /code-review finding: the charset alone is not what FileSystemToolResultStore.SanitizeSessionSegment
		// enforces — it also independently rejects a rooted path (a bare "C:" passes this charset but
		// Path.IsPathRooted measures it as drive-rooted on Windows), "." / "..", and a trailing dot
		// (which Windows silently strips, letting two different-looking ids collide onto one directory).
		// Without these here too, a value that clears this validator still throws a raw ArgumentException
		// deep in FileSystemToolResultStore on first spill — caught by the pipeline's own must-not-throw
		// handling and silently downgraded to a plain truncation marker, exactly the "late, illegible
		// failure" this validator exists to prevent for every OTHER unsafe shape.
		RuleFor(x => x.ConversationId)
			.NotEmpty().WithMessage("Conversation id is required for a durable conversation.")
			.Matches(AllowedScopeIdCharset).WithMessage("Conversation id must be 1-200 characters from [A-Za-z0-9_.:-].")
			.Must(id => id is not ("." or ".."))
				.WithMessage("Conversation id must not be a relative directory reference.")
			.Must(id => !Path.IsPathRooted(id))
				.WithMessage("Conversation id must not be an absolute or drive-rooted path.")
			.Must(id => !id.EndsWith('.'))
				.WithMessage("Conversation id must not have a trailing dot.");
	}
}
