using System.Text.RegularExpressions;
using FluentValidation;

namespace Application.Core.CQRS.Agents.RunOrchestratedTask;

/// <summary>
/// Validates <see cref="RunOrchestratedTaskCommand"/> before execution.
/// </summary>
public class RunOrchestratedTaskCommandValidator : AbstractValidator<RunOrchestratedTaskCommand>
{
	// #560: kept in sync by comment with FileSystemToolResultStore's own allowlist and
	// RunConversationCommandValidator's copy — see the latter's remarks for why this can't be a
	// shared constant across the Application/Infrastructure boundary.
	// No ':' — see RunConversationCommandValidator's identical remark: a security review found the
	// matching FileSystemToolResultStore allowlist admitted a Windows drive-rooted path via this
	// character.
	private static readonly Regex AllowedScopeIdCharset = new("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.Compiled);

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
		RuleFor(x => x.ConversationId)
			.NotEmpty().WithMessage("Conversation id is required.")
			.Matches(AllowedScopeIdCharset).WithMessage("Conversation id must be 1-128 characters from [A-Za-z0-9_.-].");
	}
}
