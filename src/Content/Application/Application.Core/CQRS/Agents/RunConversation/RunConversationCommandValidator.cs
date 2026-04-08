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
	}
}
