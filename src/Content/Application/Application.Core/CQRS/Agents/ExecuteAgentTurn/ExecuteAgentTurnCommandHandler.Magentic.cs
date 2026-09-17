using System.Diagnostics;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.Core.Orchestration.Magentic;
using Domain.AI.Agents;
using Domain.AI.Telemetry.Conventions;
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
	/// snapshot. <see cref="BuildTurnLoadedItems"/> itself is genuinely shared, not duplicated; the
	/// surrounding message/metric-recording calls in this method are a deliberately small, separately
	/// maintained copy of the equivalent calls in <see cref="Handle"/> — see the "known v1 limitations"
	/// on <see cref="Application.Core.Orchestration.Magentic.IMagenticAgentTurnRunner"/> for where that
	/// copy has already diverged (cache-hit percentage, last-call prompt tokens).
	/// </summary>
	/// <remarks>
	/// Deliberately does not build <c>ToolCallRecord</c> replay entries — see the "known v1
	/// limitation" on <see cref="Application.Core.Orchestration.Magentic.IMagenticAgentTurnRunner"/>
	/// for why a Magentic run has no single transcript to extract them from.
	/// </remarks>
	private async Task<AgentTurnResult> HandleMagenticTurnAsync(
		ExecuteAgentTurnCommand request, AgentDefinition supervisor, CancellationToken cancellationToken)
	{
		await _observabilityStore.RecordMessageAsync(
			request.ObservabilitySessionId, request.TurnNumber, "user", "user_message",
			request.UserMessage.Truncate(500), null, 0, 0, 0, 0, 0m, 0m, null,
			request.UserMessage, cancellationToken);

		var overrides = new MagenticTurnOverrides
		{
			SystemPromptOverride = request.SystemPromptOverride,
			DeploymentOverride = request.DeploymentOverride,
			Temperature = request.Temperature,
			TurnContext = request.TurnContext,
		};

		var turnSw = System.Diagnostics.Stopwatch.StartNew();
		var result = await _magenticTurnRunner.RunTurnAsync(
			supervisor, request.UserMessage, request.ConversationHistory, overrides, cancellationToken);
		turnSw.Stop();

		var agentTag = new TagList { { AgentConventions.Name, request.AgentName } };

		if (!result.Success)
		{
			_logger.LogError("Magentic supervisor {AgentName} turn {TurnNumber} failed: {Error}",
				request.AgentName, request.TurnNumber, result.Error);

			OrchestrationMetrics.TurnsTotal.Add(1, agentTag);
			OrchestrationMetrics.TurnErrors.Add(1, agentTag);

			return result;
		}

		// cacheHitPct is hardcoded 0 — AgentTurnResult carries no cache-hit-percentage field to source
		// it from (unlike CacheRead/CacheWrite, which it does carry), so a Magentic turn always reports
		// 0% here even when CacheRead is non-zero. Cosmetic in the observability store; not corrected
		// by this change.
		var assistantMessageId = await _observabilityStore.RecordMessageAsync(
			request.ObservabilitySessionId, request.TurnNumber, "assistant",
			result.ToolsInvoked.Count > 0 ? "assistant_mixed" : "assistant_text",
			result.Response.Truncate(500), result.Model,
			result.InputTokens, result.OutputTokens, result.CacheRead, result.CacheWrite,
			result.CostUsd, cacheHitPct: 0m,
			result.ToolsInvoked.Count > 0 ? result.ToolsInvoked.ToArray() : null,
			result.Response, cancellationToken);

		foreach (var toolName in result.ToolsInvoked)
		{
			ToolExecutionMetrics.Invocations.Add(1, new TagList
			{
				{ ToolConventions.Name, toolName },
				{ ToolConventions.Status, ToolConventions.StatusValues.Success }
			});

			await _observabilityStore.RecordToolExecutionAsync(
				request.ObservabilitySessionId, assistantMessageId, toolName, "magentic_participant",
				0, "success", cancellationToken: cancellationToken);
		}

		// Context snapshot: same computation the single-agent path uses (BuildTurnLoadedItems is
		// shared, not duplicated), wrapped in the same belt-and-braces try/catch — a snapshot bug
		// must not fail a turn whose answer is already produced.
		try
		{
			var (turnLoaded, turnLoadedBodies, registrations) = BuildTurnLoadedItems(
				request.ConversationId, supervisor, request.UserMessage, result.Response, result.ToolsInvoked);

			// history + this turn's user message, matching the single-agent path's #517 invariant: the
			// snapshot must reflect "the state the last call's prompt actually saw," not just prior
			// turns. request.ConversationHistory alone would undercount every Magentic turn's Messages
			// size by the message that caused it — a first turn with empty history would record zero.
			var snapshotHistory = new List<ChatMessage>(request.ConversationHistory)
			{
				new(ChatRole.User, request.UserMessage),
			};

			var snapshot = _snapshotComputer.Compute(
				conversationId: request.ConversationId,
				turnIndex: request.TurnNumber,
				turnId: $"t-{request.TurnNumber:D2}",
				history: snapshotHistory,
				registrations: registrations,
				turnLoaded: turnLoaded,
				capturedAtUtc: _timeProvider.GetUtcNow(),
				lastCallPromptTokens: null);

			await Task.WhenAll(
				_observabilityStore.RecordContextSnapshotAsync(snapshot, cancellationToken),
				_observabilityStore.RecordLoadedBodiesAsync(
					request.ConversationId, request.TurnNumber, turnLoadedBodies, cancellationToken),
				_snapshotNotifier.NotifyAsync(snapshot, cancellationToken))
				.ConfigureAwait(false);
		}
		catch (Exception snapshotEx)
		{
			_logger.LogWarning(snapshotEx,
				"Context snapshot for Magentic supervisor {AgentName} turn {TurnNumber} skipped — handler continues",
				request.AgentName, request.TurnNumber);
		}

		OrchestrationMetrics.TurnDuration.Record(turnSw.Elapsed.TotalMilliseconds, agentTag);
		OrchestrationMetrics.TurnsTotal.Add(1, agentTag);

		_logger.LogInformation(
			"Magentic supervisor {AgentName} turn {TurnNumber} completed — {InputTokens} in, {OutputTokens} out, ${Cost:F4}",
			request.AgentName, request.TurnNumber, result.InputTokens, result.OutputTokens, result.CostUsd);

		return result;
	}
}
