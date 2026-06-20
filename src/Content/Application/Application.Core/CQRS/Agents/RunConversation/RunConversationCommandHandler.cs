using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Telemetry.Conventions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Agents.RunConversation;

/// <summary>
/// Handles <see cref="RunConversationCommand"/> by executing sequential turns
/// with the specified agent, feeding each response back as context.
/// </summary>
public class RunConversationCommandHandler : IRequestHandler<RunConversationCommand, ConversationResult>
{
	private readonly IMediator _mediator;
	private readonly IAgentConversationCache _agentCache;
	private readonly IObservabilityStore _observabilityStore;
	private readonly ILogger<RunConversationCommandHandler> _logger;

	public RunConversationCommandHandler(
		IMediator mediator,
		IAgentConversationCache agentCache,
		IObservabilityStore observabilityStore,
		ILogger<RunConversationCommandHandler> logger)
	{
		_mediator = mediator;
		_agentCache = agentCache;
		_observabilityStore = observabilityStore;
		_logger = logger;
	}

	public async Task<ConversationResult> Handle(RunConversationCommand request, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Starting conversation with {AgentName}, {MessageCount} messages, max {MaxTurns} turns",
			request.AgentName, request.UserMessages.Count, request.MaxTurns);

		var sw = Stopwatch.StartNew();
		var turns = new List<TurnSummary>();
		var totalToolInvocations = 0;
		AgentTurnResult? lastResult = null;

		// Running token/cost aggregates for session-level metrics
		int totalInputTokens = 0, totalOutputTokens = 0, totalCacheRead = 0, totalCacheWrite = 0;
		decimal totalCostUsd = 0m;
		string? sessionModel = null;

		var dbSessionId = await _observabilityStore.StartSessionAsync(
			request.ConversationId, request.AgentName, null, cancellationToken);

		var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, request.AgentName);
		var sessionTags = new TagList { { AgentConventions.Name, request.AgentName } };
		SessionMetrics.SessionsStarted.Add(1, agentTag);
		SessionMetrics.ActiveSessions.Add(1, sessionTags);

		// Tracks whether the observability session has already been ended on a
		// normal (success / turn-failure) return path, so the catch block does
		// not double-end it when an exception escapes after those paths.
		var sessionEnded = false;

		try
		{
			foreach (var (userMessage, index) in request.UserMessages.Select((m, i) => (m, i)))
			{
				if (index >= request.MaxTurns)
				{
					_logger.LogWarning("Max turns ({MaxTurns}) reached for {AgentName}", request.MaxTurns, request.AgentName);
					break;
				}

				if (request.OnProgress != null)
				{
					await request.OnProgress(new TurnProgress
					{
						TurnNumber = index + 1,
						AgentName = request.AgentName,
						Status = "executing"
					});
				}

				var turnCommand = new ExecuteAgentTurnCommand
				{
					AgentName = request.AgentName,
					UserMessage = userMessage,
					ConversationHistory = lastResult?.UpdatedHistory ?? [],
					ConversationId = request.ConversationId,
					TurnNumber = index + 1,
					ObservabilitySessionId = dbSessionId
				};

				lastResult = await _mediator.Send(turnCommand, cancellationToken);

				if (!lastResult.Success)
				{
					// A cancelled turn (e.g. caller disconnect) is routine, not a failure:
					// route it into the OperationCanceledException handler below so the session
					// ends "cancelled" rather than "error", consistent with the other transports.
					if (lastResult.ErrorKind == AgentTurnErrorKind.Cancelled)
						throw new OperationCanceledException(cancellationToken);

					_logger.LogError("Conversation turn {Turn} failed for {AgentName}: {Error}",
						index + 1, request.AgentName, lastResult.Error);

					await _observabilityStore.EndSessionAsync(
						dbSessionId, "error", lastResult.Error, cancellationToken);
					sessionEnded = true;

					return new ConversationResult
					{
						Success = false,
						Turns = turns,
						FinalResponse = string.Empty,
						TotalToolInvocations = totalToolInvocations,
						Error = $"Turn {index + 1} failed: {lastResult.Error}"
					};
				}

				turns.Add(new TurnSummary
				{
					TurnNumber = index + 1,
					UserMessage = userMessage,
					AgentResponse = lastResult.Response,
					ToolsInvoked = lastResult.ToolsInvoked
				});

				totalToolInvocations += lastResult.ToolsInvoked.Count;

				totalInputTokens += lastResult.InputTokens;
				totalOutputTokens += lastResult.OutputTokens;
				totalCacheRead += lastResult.CacheRead;
				totalCacheWrite += lastResult.CacheWrite;
				totalCostUsd += lastResult.CostUsd;
				sessionModel ??= lastResult.Model;

				var totalInput = totalInputTokens + totalCacheRead;
				var cacheHitRate = totalInput > 0 ? (decimal)totalCacheRead / totalInput : 0m;

				await _observabilityStore.UpdateSessionMetricsAsync(
					dbSessionId, index + 1, totalToolInvocations, 0,
					totalInputTokens, totalOutputTokens, totalCacheRead, totalCacheWrite,
					totalCostUsd, Math.Round(cacheHitRate, 4), sessionModel, cancellationToken);

				if (request.OnProgress != null)
				{
					await request.OnProgress(new TurnProgress
					{
						TurnNumber = index + 1,
						AgentName = request.AgentName,
						Status = "completed",
						Response = lastResult.Response
					});
				}
			}

			sw.Stop();
			_logger.LogInformation("Conversation completed: {TurnCount} turns, {ToolCount} tool invocations",
				turns.Count, totalToolInvocations);

			OrchestrationMetrics.ConversationDuration.Record(sw.Elapsed.TotalMilliseconds, agentTag);
			OrchestrationMetrics.TurnsPerConversation.Record(turns.Count, agentTag);
			if (totalToolInvocations > 0)
				OrchestrationMetrics.ToolCalls.Add(totalToolInvocations, agentTag);

			if (totalCostUsd > 0)
				SessionMetrics.SessionCost.Record((double)totalCostUsd, agentTag);

			await _observabilityStore.EndSessionAsync(
				dbSessionId, "completed", null, cancellationToken);
			sessionEnded = true;

			return new ConversationResult
			{
				Success = true,
				Turns = turns,
				FinalResponse = lastResult?.Response ?? string.Empty,
				TotalToolInvocations = totalToolInvocations
			};
		}
		catch (OperationCanceledException)
		{
			// Caller cancellation (e.g. client disconnect) is routine, not exceptional.
			// End the session as cancelled using a non-cancelled token so the cleanup
			// write still completes, then rethrow to preserve cancellation semantics.
			await EndSessionSafelyAsync(dbSessionId, "cancelled", null, sessionEnded);
			sessionEnded = true;
			throw;
		}
		catch (Exception ex)
		{
			// Log the full exception via structured logging; never persist the raw
			// message to the session row (it can leak internal detail). End the
			// session with a stable scrubbed status code and rethrow.
			_logger.LogError(ex, "Conversation with {AgentName} failed with an unhandled exception",
				request.AgentName);
			await EndSessionSafelyAsync(dbSessionId, "error", "conversation.unhandled_exception", sessionEnded);
			sessionEnded = true;
			throw;
		}
		finally
		{
			// Decrement the up-down gauge exactly once on every exit path so the
			// ActiveSessions metric cannot skew permanently when the try block throws.
			SessionMetrics.ActiveSessions.Add(-1, sessionTags);
			_agentCache.Evict(request.ConversationId);
		}
	}

	/// <summary>
	/// Ends the observability session defensively during exception/cancellation
	/// handling: skips the write if the session was already ended on a normal
	/// return path, uses a non-cancelled token so cleanup completes even when the
	/// caller's token is cancelled, and never lets a cleanup failure mask the
	/// original exception being propagated.
	/// </summary>
	private async Task EndSessionSafelyAsync(Guid sessionId, string status, string? reason, bool alreadyEnded)
	{
		if (alreadyEnded)
			return;

		try
		{
			await _observabilityStore.EndSessionAsync(sessionId, status, reason, CancellationToken.None);
		}
		catch (Exception endEx)
		{
			_logger.LogError(endEx, "Failed to end observability session {SessionId} during cleanup", sessionId);
		}
	}
}
