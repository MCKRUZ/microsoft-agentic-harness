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
	private readonly IAgentMetadataRegistry _agentRegistry;
	private readonly ILogger<ExecuteAgentTurnCommandHandler> _logger;

	public ExecuteAgentTurnCommandHandler(
		IAgentFactory agentFactory,
		IAgentMetadataRegistry agentRegistry,
		ILogger<ExecuteAgentTurnCommandHandler> logger)
	{
		_agentFactory = agentFactory;
		_agentRegistry = agentRegistry;
		_logger = logger;
	}

	public async Task<AgentTurnResult> Handle(ExecuteAgentTurnCommand request, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Executing turn {TurnNumber} for agent {AgentName}",
			request.TurnNumber, request.AgentName);

		try
		{
			// AgentName from the hub is an agent id — resolve the declared skill id from the
			// AGENT.md manifest. If the manifest has no `skill:` entry or the id isn't in the
			// registry, fall back to treating AgentName as a skill id directly so callers
			// which still pass skill ids (tests, tools) keep working.
			var skillId = _agentRegistry.TryGet(request.AgentName)?.Skill ?? request.AgentName;

			var agent = await _agentFactory.CreateAgentFromSkillAsync(
				skillId,
				new SkillAgentOptions
				{
					AdditionalContext = request.SystemPromptOverride,
					DeploymentName = request.DeploymentOverride,
					Temperature = request.Temperature,
				},
				cancellationToken);

			// Build conversation messages
			var messages = new List<ChatMessage>(request.ConversationHistory)
			{
				new(ChatRole.User, request.UserMessage)
			};

			// Execute via AIAgent.RunAsync (MS Agent Framework API)
			var response = await agent.RunAsync(messages, cancellationToken: cancellationToken);

			// Extract response content and tool invocations
			var (responseText, toolsInvoked) = ExtractContentAndTools(response);

			if (toolsInvoked.Count > 0)
			{
				_logger.LogInformation("Agent {AgentName} turn {TurnNumber} invoked {ToolCount} tools: {Tools}",
					request.AgentName, request.TurnNumber, toolsInvoked.Count, string.Join(", ", toolsInvoked));
			}

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
				UpdatedHistory = updatedHistory,
				ToolsInvoked = toolsInvoked
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
	/// Extracts text content and tool invocation names from the agent RunAsync response.
	/// </summary>
	private static (string Text, IReadOnlyList<string> ToolsInvoked) ExtractContentAndTools(object? response)
	{
		if (response is null)
			return (string.Empty, []);

		if (response is string str)
			return (str, []);

		if (response is ChatResponse chatResponse)
		{
			var textParts = chatResponse.Messages
				.Where(m => m.Role == ChatRole.Assistant)
				.SelectMany(m => m.Contents.OfType<TextContent>())
				.Select(tc => tc.Text);

			var toolNames = chatResponse.Messages
				.SelectMany(m => m.Contents.OfType<FunctionCallContent>())
				.Select(fc => fc.Name)
				.Where(n => !string.IsNullOrEmpty(n))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			return (string.Join("\n", textParts), toolNames);
		}

		var contentProp = response.GetType().GetProperty("Content");
		if (contentProp != null)
			return (contentProp.GetValue(response)?.ToString() ?? string.Empty, []);

		return (response.ToString() ?? string.Empty, []);
	}
}
