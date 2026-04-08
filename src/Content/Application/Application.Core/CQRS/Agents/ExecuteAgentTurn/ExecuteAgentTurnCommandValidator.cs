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
	}
}
