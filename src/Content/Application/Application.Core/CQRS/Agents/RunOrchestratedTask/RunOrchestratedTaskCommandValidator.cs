using Domain.Common.Helpers;
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

		// #560: this command handler passes ConversationId straight through as both the durable
		// conversation id AND the tool-result retrieval scope (RunOrchestratedTaskCommandHandler ->
		// AgentExecutionContext.Initialize(..., callOnceScopeId: request.ConversationId)) with no
		// validation at all until now. The command's own default (a bare GUID) always satisfies this.
		// StorageSegmentSafety (Domain.Common.Helpers) is the single shared home for this charset and
		// its independent rooted-path / "." / ".." / trailing-dot checks — mirrors
		// RunConversationCommandValidator's identical rule; see that type's remarks for why a value
		// clearing only the charset still reaches the store as a raw, silently-downgraded
		// ArgumentException.
		RuleFor(x => x.ConversationId)
			.NotEmpty().WithMessage("Conversation id is required.")
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
