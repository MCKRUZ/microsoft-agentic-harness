using FluentValidation;

namespace Application.Core.CQRS.Agents.RunOrchestratedTask;

/// <summary>
/// Validates <see cref="RunOrchestratedTaskCommand"/> before execution.
/// </summary>
public class RunOrchestratedTaskCommandValidator : AbstractValidator<RunOrchestratedTaskCommand>
{
	public RunOrchestratedTaskCommandValidator()
	{
		RuleFor(x => x.OrchestratorName)
			.NotEmpty().WithMessage("Orchestrator name is required.");

		RuleFor(x => x.TaskDescription)
			.NotEmpty().WithMessage("Task description is required.")
			.MaximumLength(50_000).WithMessage("Task description exceeds maximum length.");

		RuleFor(x => x.AvailableAgents)
			.NotEmpty().WithMessage("At least one available agent is required.");

		RuleFor(x => x.MaxTotalTurns)
			.InclusiveBetween(1, 200).WithMessage("Max total turns must be between 1 and 200.");
	}
}
