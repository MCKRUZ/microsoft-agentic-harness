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

		// A durable conversation is addressed by this id, so it has to name something. The default is a
		// fresh GUID, which is fine for a throwaway run and meaningless for a continuing one.
		RuleFor(x => x.ConversationId)
			.NotEmpty().WithMessage("Conversation id is required for a durable conversation.")
			.When(x => x.ConversationOwnerId is not null);
	}
}
