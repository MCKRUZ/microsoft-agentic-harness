using Application.AI.Common.Interfaces;
using Domain.Common.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Agents.ExecuteAgentTurn;

/// <summary>
/// How <see cref="ExecuteAgentTurnCommandHandler"/> ends a turn that did not complete: the failed
/// <see cref="AgentTurnResult"/> carrying what the turn spent, and the per-message row that makes that
/// spend visible to the dashboards (#778, #780).
/// </summary>
public partial class ExecuteAgentTurnCommandHandler
{
	/// <summary>
	/// Builds the result for a turn that did not complete. A failed turn still reports what its model
	/// calls spent (the budget is charged from the result); <paramref name="alreadyTaken"/> is used when
	/// the run completed and drained the capture before a later step threw.
	/// </summary>
	/// <param name="assistantRowWritten">
	/// Whether the completed turn's own assistant row was already written before the step that threw. When
	/// it was, that row carries the spend and a second one for the same turn would double it in any
	/// per-message total.
	/// </param>
	private async Task<AgentTurnResult> FailedTurnAsync(
		ExecuteAgentTurnCommand request,
		IReadOnlyList<string> skillIds,
		LlmUsageSnapshot? alreadyTaken,
		bool assistantRowWritten,
		string error,
		AgentTurnErrorKind errorKind)
	{
		var usage = alreadyTaken ?? _usageCapture.TakeSnapshot();

		var failed = new AgentTurnResult
		{
			Success = false,
			Response = string.Empty,
			UpdatedHistory = [.. request.ConversationHistory, new ChatMessage(ChatRole.User, request.UserMessage)],
			Error = error,
			ErrorKind = errorKind,
			SkillIds = skillIds,
		}.WithUsage(usage);

		if (!assistantRowWritten)
			await RecordFailedAssistantMessageAsync(request, failed, usage.CacheHitPct);

		return failed;
	}

	/// <summary>
	/// Writes the per-message row for a turn that failed or was cancelled after spending (#780), so the
	/// spend the budget and the session rollup count is also visible where a dashboard drills into a
	/// conversation. A turn that spent nothing writes none — there is nothing to attribute, and it is
	/// the line the session rollup draws too.
	/// </summary>
	/// <param name="request">The turn's command.</param>
	/// <param name="failed">The failed result, already carrying the usage the turn reported.</param>
	/// <param name="cacheHitPct">The cache-hit percentage to record, when the path has one to source.</param>
	/// <remarks>
	/// Never under the caller's token — a disconnect cancels it, and this row is for exactly that turn —
	/// and a store failure is logged rather than thrown: the caller has an outcome to act on, and a
	/// dashboard row must not replace it.
	/// </remarks>
	private async Task RecordFailedAssistantMessageAsync(
		ExecuteAgentTurnCommand request, AgentTurnResult failed, decimal cacheHitPct)
	{
		if (!failed.ToTurnTelemetry().HasSpend)
			return;

		var error = failed.Error ?? string.Empty;
		try
		{
			await _observabilityStore.RecordMessageAsync(
				request.ObservabilitySessionId, request.TurnNumber, "assistant", "assistant_failed",
				error.Truncate(500), failed.Model,
				failed.InputTokens, failed.OutputTokens, failed.CacheRead, failed.CacheWrite,
				failed.CostUsd, cacheHitPct,
				failed.ToolsInvoked.Count > 0 ? failed.ToolsInvoked.ToArray() : null,
				error, CancellationToken.None);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex,
				"Could not record failed turn {TurnNumber} of conversation {ConversationId} to the observability store",
				request.TurnNumber, request.ConversationId);
		}
	}
}
