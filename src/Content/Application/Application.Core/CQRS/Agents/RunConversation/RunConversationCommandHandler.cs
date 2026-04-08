using Application.Core.CQRS.Agents.ExecuteAgentTurn;
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
	private readonly ILogger<RunConversationCommandHandler> _logger;

	public RunConversationCommandHandler(
		IMediator mediator,
		ILogger<RunConversationCommandHandler> logger)
	{
		_mediator = mediator;
		_logger = logger;
	}

	public async Task<ConversationResult> Handle(RunConversationCommand request, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Starting conversation with {AgentName}, {MessageCount} messages, max {MaxTurns} turns",
			request.AgentName, request.UserMessages.Count, request.MaxTurns);

		var turns = new List<TurnSummary>();
		var totalToolInvocations = 0;
		AgentTurnResult? lastResult = null;

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
				TurnNumber = index + 1
			};

			lastResult = await _mediator.Send(turnCommand, cancellationToken);

			if (!lastResult.Success)
			{
				_logger.LogError("Conversation turn {Turn} failed for {AgentName}: {Error}",
					index + 1, request.AgentName, lastResult.Error);

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

		_logger.LogInformation("Conversation completed: {TurnCount} turns, {ToolCount} tool invocations",
			turns.Count, totalToolInvocations);

		return new ConversationResult
		{
			Success = true,
			Turns = turns,
			FinalResponse = lastResult?.Response ?? string.Empty,
			TotalToolInvocations = totalToolInvocations
		};
	}
}
