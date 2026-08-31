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
	// shared constant across the Application/Infrastructure boundary, why ':' is admitted
	// (PlanRunKeys.StepConversationId's "{runScope}:{stepId}" shape) with Path.IsPathRooted, not this
	// charset, as the actual backstop against a drive-rooted id, and why the length bound is 200
	// (the derived shape can reach 165 characters) rather than the 128 an earlier version used.
	private static readonly Regex AllowedScopeIdCharset = new(@"\A[A-Za-z0-9_.:-]{1,200}\z", RegexOptions.Compiled);

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
		// /code-review finding: mirrors RunConversationCommandValidator's identical addition — the
		// charset alone is not what FileSystemToolResultStore.SanitizeSessionSegment enforces; it also
		// independently rejects a rooted path, "." / "..", and a trailing dot. See that validator's
		// remarks for why a value clearing only the charset still reaches the store as a raw,
		// silently-downgraded ArgumentException.
		RuleFor(x => x.ConversationId)
			.NotEmpty().WithMessage("Conversation id is required.")
			.Matches(AllowedScopeIdCharset).WithMessage("Conversation id must be 1-200 characters from [A-Za-z0-9_.:-].")
			.Must(id => id is not ("." or ".."))
				.WithMessage("Conversation id must not be a relative directory reference.")
			.Must(id => !Path.IsPathRooted(id))
				.WithMessage("Conversation id must not be an absolute or drive-rooted path.")
			.Must(id => !id.EndsWith('.'))
				.WithMessage("Conversation id must not have a trailing dot.");
	}
}
