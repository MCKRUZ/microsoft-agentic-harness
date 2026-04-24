using System.Diagnostics;
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
	private readonly IObservabilityStore _observabilityStore;
	private readonly ILogger<RunConversationCommandHandler> _logger;

	public RunConversationCommandHandler(
		IMediator mediator,
		IObservabilityStore observabilityStore,
		ILogger<RunConversationCommandHandler> logger)
	{
		_mediator = mediator;
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

		foreach (var (userMessage, index) in request.UserMessages.Select((m, i) => (m, i)))
		{
			if (index >= request.MaxTurns)
			{
				_logger.LogWarning("Max turns ({MaxTurns}) reached for {AgentName}", request.MaxTurns, request.AgentName);
				break;
			}

			// Report progress
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
				_logger.LogError("Conversation turn {Turn} failed for {AgentName}: {Error}",
					index + 1, request.AgentName, lastResult.Error);

				await _observabilityStore.EndSessionAsync(
					dbSessionId, "error", lastResult.Error, cancellationToken);

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

			// Accumulate token/cost metrics from this turn
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

			// Report progress with response
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

		var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, request.AgentName);
		OrchestrationMetrics.ConversationDuration.Record(sw.Elapsed.TotalMilliseconds, agentTag);
		OrchestrationMetrics.TurnsPerConversation.Record(turns.Count, agentTag);
		if (totalToolInvocations > 0)
			OrchestrationMetrics.ToolCalls.Add(totalToolInvocations, agentTag);

		await _observabilityStore.EndSessionAsync(
			dbSessionId, "completed", null, cancellationToken);

		return new ConversationResult
		{
			Success = true,
			Turns = turns,
			FinalResponse = lastResult?.Response ?? string.Empty,
			TotalToolInvocations = totalToolInvocations
		};
	}
}
