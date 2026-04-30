using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Services;
using Domain.AI.Skills;
using Domain.Common.Extensions;
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
	private readonly IObservabilityStore _observabilityStore;
	private readonly ILlmUsageCapture _usageCapture;
	private readonly ILogger<ExecuteAgentTurnCommandHandler> _logger;

	public ExecuteAgentTurnCommandHandler(
		IAgentFactory agentFactory,
		IAgentMetadataRegistry agentRegistry,
		IObservabilityStore observabilityStore,
		ILlmUsageCapture usageCapture,
		ILogger<ExecuteAgentTurnCommandHandler> logger)
	{
		_agentFactory = agentFactory;
		_agentRegistry = agentRegistry;
		_observabilityStore = observabilityStore;
		_usageCapture = usageCapture;
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

			await _observabilityStore.RecordMessageAsync(
				request.ObservabilitySessionId, request.TurnNumber, "user", "user_message",
				request.UserMessage.Truncate(500), null, 0, 0, 0, 0, 0m, 0m, null, cancellationToken);

			// Clear stale usage data before the agent turn
			_usageCapture.TakeSnapshot();

			// Set ambient capture so the singleton-scoped ObservabilityMiddleware
			// records to this handler's scoped ILlmUsageCapture instance.
			LlmUsageCapture.Current = _usageCapture;

			object? response;
			var turnSw = Stopwatch.StartNew();
			try
			{
				response = await agent.RunAsync(messages, cancellationToken: cancellationToken);
				turnSw.Stop();
			}
			finally
			{
				LlmUsageCapture.Current = null;
			}

			// Capture accumulated token usage from all LLM calls during this turn
			var usage = _usageCapture.TakeSnapshot();

			// Extract response text; tool names come from the ambient capture
			var responseText = ExtractResponseText(response);
			var toolsInvoked = usage.ToolNames;

			if (toolsInvoked.Count > 0)
			{
				_logger.LogInformation("Agent {AgentName} turn {TurnNumber} invoked {ToolCount} tools: {Tools}",
					request.AgentName, request.TurnNumber, toolsInvoked.Count, string.Join(", ", toolsInvoked));
			}

			var source = toolsInvoked.Count > 0 ? "assistant_mixed" : "assistant_text";
			await _observabilityStore.RecordMessageAsync(
				request.ObservabilitySessionId, request.TurnNumber, "assistant", source,
				responseText.Truncate(500), usage.Model,
				usage.InputTokens, usage.OutputTokens, usage.CacheRead, usage.CacheWrite,
				usage.CostUsd, usage.CacheHitPct,
				toolsInvoked.Count > 0 ? toolsInvoked.ToArray() : null, cancellationToken);

			foreach (var toolName in toolsInvoked)
			{
				await _observabilityStore.RecordToolExecutionAsync(
					request.ObservabilitySessionId, null, toolName, "keyed_di",
					0, "success", null, null, cancellationToken);
			}

			// Build updated history (add user message + assistant response)
			var updatedHistory = new List<ChatMessage>(messages)
			{
				new(ChatRole.Assistant, responseText)
			};

			_logger.LogInformation("Agent {AgentName} turn {TurnNumber} completed — {InputTokens} in, {OutputTokens} out, ${Cost:F4}",
				request.AgentName, request.TurnNumber, usage.InputTokens, usage.OutputTokens, usage.CostUsd);

			return new AgentTurnResult
			{
				Success = true,
				Response = responseText,
				UpdatedHistory = updatedHistory,
				ToolsInvoked = toolsInvoked,
				InputTokens = usage.InputTokens,
				OutputTokens = usage.OutputTokens,
				CacheRead = usage.CacheRead,
				CacheWrite = usage.CacheWrite,
				CostUsd = usage.CostUsd,
				Model = usage.Model
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
				Error = "An internal error occurred during the agent turn."
			};
		}
	}


	/// <summary>
	/// Extracts text content from the agent RunAsync response.
	/// Handles <see cref="AgentResponse"/>, <see cref="ChatResponse"/>, string, and reflection fallbacks.
	/// </summary>
	private static string ExtractResponseText(object? response)
	{
		if (response is null)
			return string.Empty;

		if (response is string str)
			return str;

		if (response is AgentResponse agentResponse)
			return agentResponse.Text ?? string.Empty;

		if (response is ChatResponse chatResponse)
		{
			var textParts = chatResponse.Messages
				.Where(m => m.Role == ChatRole.Assistant)
				.SelectMany(m => m.Contents.OfType<TextContent>())
				.Select(tc => tc.Text);

			return string.Join("\n", textParts);
		}

		var textProp = response.GetType().GetProperty("Text")
			?? response.GetType().GetProperty("Content");
		if (textProp != null)
			return textProp.GetValue(response)?.ToString() ?? string.Empty;

		return response.ToString() ?? string.Empty;
	}
}
