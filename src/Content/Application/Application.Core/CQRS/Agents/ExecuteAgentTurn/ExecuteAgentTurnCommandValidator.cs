using FluentValidation;

namespace Application.Core.CQRS.Agents.ExecuteAgentTurn;

/// <summary>
/// Validates <see cref="ExecuteAgentTurnCommand"/> before execution.
/// </summary>
public class ExecuteAgentTurnCommandValidator : AbstractValidator<ExecuteAgentTurnCommand>
{
	public ExecuteAgentTurnCommandValidator()
	{
		RuleFor(x => x.AgentName)
			.NotEmpty().WithMessage("Agent name is required.");

		RuleFor(x => x.UserMessage)
			.NotEmpty().WithMessage("User message is required.")
			.MaximumLength(100_000).WithMessage("User message exceeds maximum length.");

		// Screened with the message (#904), so its size sets how many content-safety calls a turn makes; bounded like
		// the message so a caller cannot turn one turn into thousands of them.
		RuleFor(x => x.TurnContext)
			.MaximumLength(100_000).WithMessage("Turn context exceeds maximum length.");
	}
}
