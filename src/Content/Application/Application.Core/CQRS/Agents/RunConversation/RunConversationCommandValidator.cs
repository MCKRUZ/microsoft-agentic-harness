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
	// No ':' — a security review found FileSystemToolResultStore.SanitizeSessionSegment's matching
	// allowlist admitted "C:" / "C:foo" as Path.IsPathRooted==true on Windows, escaping StoragePath
	// entirely once Path.Combine hit a rooted segment. The store's own Path.IsPathRooted check is the
	// real backstop regardless of what this validator allows; this charset is kept narrow to match it
	// rather than to be the enforcement point itself.
	private static readonly Regex AllowedScopeIdCharset = new("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.Compiled);

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
		RuleFor(x => x.ConversationId)
			.NotEmpty().WithMessage("Conversation id is required for a durable conversation.")
			.Matches(AllowedScopeIdCharset).WithMessage("Conversation id must be 1-128 characters from [A-Za-z0-9_.-].");
	}
}
