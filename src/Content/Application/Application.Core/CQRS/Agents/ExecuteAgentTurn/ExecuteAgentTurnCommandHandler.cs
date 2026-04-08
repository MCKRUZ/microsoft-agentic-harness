using Application.AI.Common.Interfaces;
using Domain.AI.Skills;
using MediatR;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Agents.ExecuteAgentTurn;

/// <summary>
/// Handles <see cref="ExecuteAgentTurnCommand"/> by creating an agent
/// and executing a single conversation turn via the MS Agent Framework.
/// </summary>
public class ExecuteAgentTurnCommandHandler : IRequestHandler<ExecuteAgentTurnCommand, AgentTurnResult>
{
	private readonly IAgentFactory _agentFactory;
	private readonly ILogger<ExecuteAgentTurnCommandHandler> _logger;

	public ExecuteAgentTurnCommandHandler(
		IAgentFactory agentFactory,
		ILogger<ExecuteAgentTurnCommandHandler> logger)
	{
		_agentFactory = agentFactory;
		_logger = logger;
	}

	public async Task<AgentTurnResult> Handle(ExecuteAgentTurnCommand request, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Executing turn {TurnNumber} for agent {AgentName}",
			request.TurnNumber, request.AgentName);

		try
		{
			var agent = await _agentFactory.CreateAgentFromSkillAsync(
				request.AgentName,
				new SkillAgentOptions { AdditionalContext = request.SystemPromptOverride },
				cancellationToken);

			// Build conversation messages
			var messages = new List<ChatMessage>(request.ConversationHistory)
			{
				new(ChatRole.User, request.UserMessage)
			};

			// Execute via AIAgent.RunAsync (MS Agent Framework API)
			var response = await agent.RunAsync(messages, cancellationToken: cancellationToken);

			// Extract response content
			var responseText = ExtractContent(response);

			// Build updated history (add user message + assistant response)
			var updatedHistory = new List<ChatMessage>(messages)
			{
				new(ChatRole.Assistant, responseText)
			};

			_logger.LogInformation("Agent {AgentName} turn {TurnNumber} completed",
				request.AgentName, request.TurnNumber);

			return new AgentTurnResult
			{
				Success = true,
				Response = responseText,
				UpdatedHistory = updatedHistory
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Agent {AgentName} turn {TurnNumber} failed", request.AgentName, request.TurnNumber);

			return new AgentTurnResult
			{
				Success = false,
				Response = string.Empty,
				UpdatedHistory = [.. request.ConversationHistory, new ChatMessage(ChatRole.User, request.UserMessage)],
				Error = ex.Message
			};
		}
	}

	/// <summary>
	/// Extracts text content from the agent RunAsync response.
	/// </summary>
	private static string ExtractContent(object? response)
	{
		if (response is null)
			return string.Empty;

		if (response is string str)
			return str;

		// Handle ChatResponse from the framework
		if (response is ChatResponse chatResponse)
		{
			var textParts = chatResponse.Messages
				.Where(m => m.Role == ChatRole.Assistant)
				.SelectMany(m => m.Contents.OfType<TextContent>())
				.Select(tc => tc.Text);
			return string.Join("\n", textParts);
		}

		// Fallback: try Content property via reflection
		var contentProp = response.GetType().GetProperty("Content");
		if (contentProp != null)
			return contentProp.GetValue(response)?.ToString() ?? string.Empty;

		return response.ToString() ?? string.Empty;
	}
}
