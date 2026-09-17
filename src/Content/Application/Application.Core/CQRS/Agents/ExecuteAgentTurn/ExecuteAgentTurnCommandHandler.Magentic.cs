using System.Diagnostics;
using Application.Core.Orchestration.Magentic;
using Domain.AI.Agents;
using Domain.Common.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Agents.ExecuteAgentTurn;

/// <summary>
/// The <see cref="AgentOrchestrationMode.Magentic"/> branch of <see cref="ExecuteAgentTurnCommandHandler"/>.
/// See the class-level remarks on the main file for why this branches from one chokepoint instead
/// of a second command type.
/// </summary>
public partial class ExecuteAgentTurnCommandHandler
{
	/// <summary>
	/// Runs a supervisor agent's turn via <see cref="IMagenticAgentTurnRunner"/> and records it the
	/// same way a single-agent turn is recorded — message rows, tool-execution metrics, and a context
	/// snapshot — by calling the exact same shared private methods <see cref="Handle"/> calls
	/// (<see cref="RecordUserMessageAsync"/>, <see cref="RecordSimpleToolInvocationsAsync"/>,
	/// <see cref="RecordContextSnapshotAsync"/>, <see cref="RecordTurnCompletionMetrics"/>), not
	/// separately maintained copies of them. The one recording step that stays genuinely separate is
	/// the assistant-message record just below — see its own comment for why.
	/// </summary>
	/// <remarks>
	/// Deliberately does not build <c>ToolCallRecord</c> replay entries — see the "known v1
	/// limitation" on <see cref="Application.Core.Orchestration.Magentic.IMagenticAgentTurnRunner"/>
	/// for why a Magentic run has no single transcript to extract them from.
	/// </remarks>
	private async Task<AgentTurnResult> HandleMagenticTurnAsync(
		ExecuteAgentTurnCommand request, AgentDefinition supervisor, CancellationToken cancellationToken)
	{
		await RecordUserMessageAsync(request, cancellationToken);

		var overrides = new MagenticTurnOverrides
		{
			SystemPromptOverride = request.SystemPromptOverride,
			DeploymentOverride = request.DeploymentOverride,
			Temperature = request.Temperature,
			TurnContext = request.TurnContext,
		};

		var turnSw = Stopwatch.StartNew();
		var result = await _magenticTurnRunner.RunTurnAsync(
			supervisor, request.UserMessage, request.ConversationHistory, overrides, cancellationToken);
		turnSw.Stop();

		if (!result.Success)
		{
			_logger.LogError("Magentic supervisor {AgentName} turn {TurnNumber} failed: {Error}",
				request.AgentName, request.TurnNumber, result.Error);

			RecordTurnError(request.AgentName);
			return result;
		}

		// Kept separate from the single-agent path's equivalent call rather than folded into a shared
		// helper: cacheHitPct is hardcoded 0 here (AgentTurnResult carries no cache-hit-percentage
		// field to source it from, unlike the single-agent path's usage.CacheHitPct) and there is no
		// per-invocation detail to pass — a real capability gap, not incidental duplication, so a
		// shared helper would either grow a leaky "Magentic mode" parameter or hide the asymmetry.
		var assistantMessageId = await _observabilityStore.RecordMessageAsync(
			request.ObservabilitySessionId, request.TurnNumber, "assistant",
			result.ToolsInvoked.Count > 0 ? "assistant_mixed" : "assistant_text",
			result.Response.Truncate(500), result.Model,
			result.InputTokens, result.OutputTokens, result.CacheRead, result.CacheWrite,
			result.CostUsd, cacheHitPct: 0m,
			result.ToolsInvoked.Count > 0 ? result.ToolsInvoked.ToArray() : null,
			result.Response, cancellationToken);

		await RecordSimpleToolInvocationsAsync(
			request.ObservabilitySessionId, assistantMessageId, result.ToolsInvoked, "magentic_participant",
			cancellationToken);

		// history + this turn's user message, matching the single-agent path's #517 invariant: the
		// snapshot must reflect "the state the last call's prompt actually saw," not just prior turns.
		// request.ConversationHistory alone would undercount every Magentic turn's Messages size by the
		// message that caused it — a first turn with empty history would record zero.
		var snapshotHistory = new List<ChatMessage>(request.ConversationHistory)
		{
			new(ChatRole.User, request.UserMessage),
		};

		// lastCallPromptTokens: null — the Magentic path doesn't source this today; see
		// IMagenticAgentTurnRunner's remarks.
		await RecordContextSnapshotAsync(
			request.ConversationId, request.TurnNumber, supervisor, request.UserMessage, result.Response,
			result.ToolsInvoked, snapshotHistory, lastCallPromptTokens: null, request.AgentName,
			cancellationToken);

		RecordTurnCompletionMetrics(
			request.AgentName, request.TurnNumber, turnSw.Elapsed,
			result.InputTokens, result.OutputTokens, result.CostUsd);

		return result;
	}
}
