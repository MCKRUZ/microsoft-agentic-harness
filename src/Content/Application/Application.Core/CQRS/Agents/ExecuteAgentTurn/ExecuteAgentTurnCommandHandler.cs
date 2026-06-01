using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services;
using Domain.AI.Context;
using Domain.AI.Skills;
using Domain.AI.Telemetry.Conventions;
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
	private readonly IAgentConversationCache _agentCache;
	private readonly IAgentMetadataRegistry _agentRegistry;
	private readonly IObservabilityStore _observabilityStore;
	private readonly ILlmUsageCapture _usageCapture;
	private readonly IContextSnapshotComputer _snapshotComputer;
	private readonly IContextSnapshotNotifier _snapshotNotifier;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger<ExecuteAgentTurnCommandHandler> _logger;

	public ExecuteAgentTurnCommandHandler(
		IAgentConversationCache agentCache,
		IAgentMetadataRegistry agentRegistry,
		IObservabilityStore observabilityStore,
		ILlmUsageCapture usageCapture,
		IContextSnapshotComputer snapshotComputer,
		IContextSnapshotNotifier snapshotNotifier,
		TimeProvider timeProvider,
		ILogger<ExecuteAgentTurnCommandHandler> logger)
	{
		_agentCache = agentCache;
		_agentRegistry = agentRegistry;
		_observabilityStore = observabilityStore;
		_usageCapture = usageCapture;
		_snapshotComputer = snapshotComputer;
		_snapshotNotifier = snapshotNotifier;
		_timeProvider = timeProvider;
		_logger = logger;
	}

	public async Task<AgentTurnResult> Handle(ExecuteAgentTurnCommand request, CancellationToken cancellationToken)
	{
		Activity.Current?.SetTag(AgentConventions.Name, request.AgentName);
		Activity.Current?.AddBaggage(AgentConventions.Name, request.AgentName);

		_logger.LogInformation("Executing turn {TurnNumber} for agent {AgentName}",
			request.TurnNumber, request.AgentName);

		try
		{
			// AgentName from the hub is an agent id — resolve the declared skill ids from the
			// AGENT.md manifest. If the manifest has no `skills:` entry or the id isn't in the
			// registry, fall back to treating AgentName as a skill id directly so callers
			// which still pass skill ids (tests, tools) keep working.
			var agentDef = _agentRegistry.TryGet(request.AgentName);
			IReadOnlyList<string> skillIds = agentDef?.Skills is { Count: > 0 }
				? agentDef.Skills
				: [request.AgentName];

			var agent = await _agentCache.GetOrCreateAsync(
				request.ConversationId,
				skillIds,
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
				ToolExecutionMetrics.Invocations.Add(1, new TagList
				{
					{ ToolConventions.Name, toolName },
					{ ToolConventions.Status, ToolConventions.StatusValues.Success }
				});

				await _observabilityStore.RecordToolExecutionAsync(
					request.ObservabilitySessionId, null, toolName, "keyed_di",
					0, "success", null, null, cancellationToken);
			}

			// Build updated history (add user message + assistant response)
			var updatedHistory = new List<ChatMessage>(messages)
			{
				new(ChatRole.Assistant, responseText)
			};

			// Foresight: compute, persist, and notify the per-turn context snapshot.
			// Persistence + broadcast run concurrently because the broadcast does not
			// depend on the persist result — live observers shouldn't wait on the
			// DB round-trip, and a persist failure shouldn't suppress the broadcast.
			// The wrapping try/catch is belt-and-braces so a bug in any of the three
			// (compute, persist, notify) can never fail the turn.
			try
			{
				var turnLoaded = BuildTurnLoadedItems(request.UserMessage, responseText, toolsInvoked);
				var snapshot = _snapshotComputer.Compute(
					conversationId: request.ConversationId,
					turnIndex: request.TurnNumber,
					turnId: $"t-{request.TurnNumber:D2}",
					inputTokens: usage.InputTokens,
					history: updatedHistory,
					turnLoaded: turnLoaded,
					capturedAtUtc: _timeProvider.GetUtcNow());

				await Task.WhenAll(
					_observabilityStore.RecordContextSnapshotAsync(snapshot, cancellationToken),
					_snapshotNotifier.NotifyAsync(snapshot, cancellationToken))
					.ConfigureAwait(false);
			}
			catch (Exception snapshotEx)
			{
				_logger.LogWarning(snapshotEx,
					"Context snapshot for agent {AgentName} turn {TurnNumber} skipped — handler continues",
					request.AgentName, request.TurnNumber);
			}

			var agentTag = new TagList { { AgentConventions.Name, request.AgentName } };
			OrchestrationMetrics.TurnDuration.Record(turnSw.Elapsed.TotalMilliseconds, agentTag);
			OrchestrationMetrics.TurnsTotal.Add(1, agentTag);

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

			var errorTag = new TagList { { AgentConventions.Name, request.AgentName } };
			OrchestrationMetrics.TurnsTotal.Add(1, errorTag);
			OrchestrationMetrics.TurnErrors.Add(1, errorTag);

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
	/// Builds the per-turn <see cref="LoadedItem"/> delta — the artifacts that
	/// arrived in the model's context window this turn (user message, assistant
	/// response, tool invocations).
	/// </summary>
	/// <remarks>
	/// User and assistant messages are sized via
	/// <see cref="TokenEstimationHelper.EstimateTokens(string)"/>.
	/// Tool entries currently carry 0 tokens — the LLM SDK's tool-call surface
	/// does not expose result payloads to the handler, so per-tool sizing
	/// is recorded as 0 and tools register as items only. A follow-up that
	/// captures tool result text can populate the token field.
	/// </remarks>
	private static IReadOnlyList<LoadedItem> BuildTurnLoadedItems(
		string userMessage,
		string assistantResponse,
		IReadOnlyList<string> toolsInvoked)
	{
		var items = new List<LoadedItem>(2 + toolsInvoked.Count)
		{
			new(
				What: "User message",
				Tokens: TokenEstimationHelper.EstimateTokens(userMessage),
				Category: ContextCategory.Messages,
				Reference: null),
			new(
				What: "Assistant message",
				Tokens: TokenEstimationHelper.EstimateTokens(assistantResponse),
				Category: ContextCategory.Messages,
				Reference: null),
		};

		foreach (var toolName in toolsInvoked)
		{
			items.Add(new LoadedItem(
				What: $"Tool: {toolName}",
				Tokens: 0,
				Category: ContextCategory.Messages,
				Reference: toolName));
		}

		return items;
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
