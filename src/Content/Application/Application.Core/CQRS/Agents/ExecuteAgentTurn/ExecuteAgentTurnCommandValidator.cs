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

		// Screened with the message (#904), so together they set how many content-safety calls a turn makes; bounded as
		// one so a single turn stays within the same budget a maximum-length message alone would use (#959). Measured on
		// what the caller sent, so a turn context that is not screened (blank) still counts.
		RuleFor(x => x.TurnContext)
			.Must((command, context) => (command.UserMessage?.Length ?? 0) + (context?.Length ?? 0) <= 100_000)
			.WithMessage("Message and turn context together exceed the maximum length.")
			.When(x => !string.IsNullOrEmpty(x.TurnContext));
	}
}
