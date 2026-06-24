using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Governance;
using Domain.AI.Agents;
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
	private readonly IToolInvocationGovernor _governor;
	private readonly IAgentMetadataRegistry _agentRegistry;
	private readonly ISkillMetadataRegistry _skillRegistry;
	private readonly IConversationRegistrationTracker _registrationTracker;
	private readonly IObservabilityStore _observabilityStore;
	private readonly ILlmUsageCapture _usageCapture;
	private readonly IContextSnapshotComputer _snapshotComputer;
	private readonly IContextSnapshotNotifier _snapshotNotifier;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger<ExecuteAgentTurnCommandHandler> _logger;

	public ExecuteAgentTurnCommandHandler(
		IAgentConversationCache agentCache,
		IToolInvocationGovernor governor,
		IAgentMetadataRegistry agentRegistry,
		ISkillMetadataRegistry skillRegistry,
		IConversationRegistrationTracker registrationTracker,
		IObservabilityStore observabilityStore,
		ILlmUsageCapture usageCapture,
		IContextSnapshotComputer snapshotComputer,
		IContextSnapshotNotifier snapshotNotifier,
		TimeProvider timeProvider,
		ILogger<ExecuteAgentTurnCommandHandler> logger)
	{
		_agentCache = agentCache;
		_governor = governor;
		_agentRegistry = agentRegistry;
		_skillRegistry = skillRegistry;
		_registrationTracker = registrationTracker;
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
				request.UserMessage.Truncate(500), null, 0, 0, 0, 0, 0m, 0m, null,
				request.UserMessage, cancellationToken);

			// Clear stale usage data before the agent turn
			_usageCapture.TakeSnapshot();

			// Set ambient capture so the singleton-scoped ObservabilityMiddleware
			// records to this handler's scoped ILlmUsageCapture instance.
			LlmUsageCapture.Current = _usageCapture;

			// Same bridge for tool governance: the agent (and its cached tool functions) outlive
			// this scope, so expose this turn's scoped governor ambiently for the governed tool
			// wrapper to consult at invocation time. Reset first: nested MediatR sends within a
			// conversation share one scope (one governor), so clear prior turns' decisions so this
			// turn's trace reflects only this turn — mirrors the _usageCapture clear above.
			_governor.Reset();
			ToolGovernanceAccessor.Current = _governor;

			object? response;
			var turnSw = Stopwatch.StartNew();
			try
			{
				// When a transport has attached a streaming sink, stream assistant text
				// deltas as the model generates them (real perceived-latency win). Usage
				// and tool capture still flow through the chat-client middleware, so the
				// post-turn accounting below is identical to the blocking path. With no
				// sink (tests, batch callers) fall back to a single blocking call.
				var streamSink = AgentTurnStreamSink.Current;
				response = streamSink is not null
					? await RunStreamingTurnAsync(agent, messages, streamSink, cancellationToken)
					: await agent.RunAsync(messages, cancellationToken: cancellationToken);
				turnSw.Stop();
			}
			finally
			{
				LlmUsageCapture.Current = null;
				ToolGovernanceAccessor.Current = null;
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
			var assistantMessageId = await _observabilityStore.RecordMessageAsync(
				request.ObservabilitySessionId, request.TurnNumber, "assistant", source,
				responseText.Truncate(500), usage.Model,
				usage.InputTokens, usage.OutputTokens, usage.CacheRead, usage.CacheWrite,
				usage.CostUsd, usage.CacheHitPct,
				toolsInvoked.Count > 0 ? toolsInvoked.ToArray() : null,
				responseText, cancellationToken);

			// Pair captured per-CallId invocations with the assistant message that
			// requested them so the per-invocation deep-link can resolve back to
			// its parent. Fall back to the simple name-only path when no
			// invocations were captured (mostly tests with mocked middleware).
			if (usage.ToolInvocations.Count > 0)
			{
				foreach (var inv in usage.ToolInvocations)
				{
					ToolExecutionMetrics.Invocations.Add(1, new TagList
					{
						{ ToolConventions.Name, inv.ToolName },
						{ ToolConventions.Status, ToolConventions.StatusValues.Success }
					});

					await _observabilityStore.RecordToolExecutionAsync(
						request.ObservabilitySessionId, assistantMessageId,
						inv.ToolName, "keyed_di",
						0, "success", null, inv.Stdout?.Length,
						inv.CallId, inv.ArgsJson, inv.Stdout, cancellationToken);
				}
			}
			else
			{
				foreach (var toolName in toolsInvoked)
				{
					ToolExecutionMetrics.Invocations.Add(1, new TagList
					{
						{ ToolConventions.Name, toolName },
						{ ToolConventions.Status, ToolConventions.StatusValues.Success }
					});

					await _observabilityStore.RecordToolExecutionAsync(
						request.ObservabilitySessionId, assistantMessageId, toolName, "keyed_di",
						0, "success", cancellationToken: cancellationToken);
				}
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
				var (turnLoaded, turnLoadedBodies) = BuildTurnLoadedItems(
					request.ConversationId,
					agentDef,
					request.UserMessage,
					responseText,
					toolsInvoked);
				var snapshot = _snapshotComputer.Compute(
					conversationId: request.ConversationId,
					turnIndex: request.TurnNumber,
					turnId: $"t-{request.TurnNumber:D2}",
					inputTokens: usage.InputTokens,
					history: updatedHistory,
					turnLoaded: turnLoaded,
					capturedAtUtc: _timeProvider.GetUtcNow());

				// RecordLoadedBodiesAsync writes to the context_snapshot_loaded_bodies
				// sidecar table — keeps the snapshot row + SignalR wire small (just
				// labels + token counts) while still making the full prompt / skill /
				// tool-schema text available to the drawer via the lazy
				// GET /sessions/:id/turns/:turn/loaded/:idx/body endpoint.
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
				Model = usage.Model,
				Governance = _governor.GetTrace()
			};
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The caller's token cancelled mid-turn (e.g. client disconnect). Routine
			// control flow, not an agent failure: tag it Cancelled so the transport can
			// abort quietly instead of recording a health error. Deliberately not counted
			// via RecordTurnError. A per-request timeout cancels a linked token (this
			// handler's token stays uncancelled), so it never lands here — it surfaces as a
			// TimeoutException and is classified Internal upstream.
			_logger.LogInformation("Agent {AgentName} turn {TurnNumber} cancelled by caller",
				request.AgentName, request.TurnNumber);

			return new AgentTurnResult
			{
				Success = false,
				Response = string.Empty,
				UpdatedHistory = [.. request.ConversationHistory, new ChatMessage(ChatRole.User, request.UserMessage)],
				Error = "The agent turn was cancelled.",
				ErrorKind = AgentTurnErrorKind.Cancelled
			};
		}
		catch (Exception ex) when (FindConfigurationError(ex) is { } configError)
		{
			_logger.LogError(ex,
				"Agent {AgentName} turn {TurnNumber} failed — AI provider not configured",
				request.AgentName, request.TurnNumber);

			RecordTurnError(request.AgentName);

			return new AgentTurnResult
			{
				Success = false,
				Response = string.Empty,
				UpdatedHistory = [.. request.ConversationHistory, new ChatMessage(ChatRole.User, request.UserMessage)],
				Error = configError.Message,
				ErrorKind = AgentTurnErrorKind.Configuration
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Agent {AgentName} turn {TurnNumber} failed", request.AgentName, request.TurnNumber);

			RecordTurnError(request.AgentName);

			return new AgentTurnResult
			{
				Success = false,
				Response = string.Empty,
				UpdatedHistory = [.. request.ConversationHistory, new ChatMessage(ChatRole.User, request.UserMessage)],
				Error = "An internal error occurred during the agent turn.",
				ErrorKind = AgentTurnErrorKind.Internal
			};
		}
	}

	/// <summary>
	/// Runs the turn in streaming mode, emitting each assistant text delta to
	/// <paramref name="sink"/> as it arrives and returning the concatenated full text.
	/// Tool invocation, usage, and tool-call capture happen transparently in the
	/// chat-client middleware pipeline (which instruments the streaming path), so the
	/// caller's post-turn accounting is unchanged. The same <paramref name="cancellationToken"/>
	/// flows to <c>RunStreamingAsync</c>, so a disconnected consumer aborts the model call.
	/// </summary>
	private static async Task<string> RunStreamingTurnAsync(
		AIAgent agent,
		IReadOnlyList<ChatMessage> messages,
		IAgentTurnStreamSink sink,
		CancellationToken cancellationToken)
	{
		var builder = new StringBuilder();
		await foreach (var update in agent.RunStreamingAsync(messages, cancellationToken: cancellationToken))
		{
			var delta = update.Text;
			if (string.IsNullOrEmpty(delta))
				continue;

			builder.Append(delta);
			await sink.EmitAsync(delta, cancellationToken);
		}

		return builder.ToString();
	}

	private static void RecordTurnError(string agentName)
	{
		var errorTag = new TagList { { AgentConventions.Name, agentName } };
		OrchestrationMetrics.TurnsTotal.Add(1, errorTag);
		OrchestrationMetrics.TurnErrors.Add(1, errorTag);
	}

	/// <summary>
	/// Walks the exception chain for an <see cref="AiProviderNotConfiguredException"/> so a
	/// provider-misconfiguration is classified even when the agent pipeline wraps it.
	/// </summary>
	private static AiProviderNotConfiguredException? FindConfigurationError(Exception? ex)
	{
		for (; ex is not null; ex = ex.InnerException)
			if (ex is AiProviderNotConfiguredException configError)
				return configError;
		return null;
	}


	/// <summary>
	/// Builds the per-turn <see cref="LoadedItem"/> delta — the artifacts that arrived
	/// in the model's context window this turn. On the first turn (or whenever a
	/// registration changes mid-conversation) this includes the System prompt, Skills,
	/// native Tools, MCP Tools, and Sub-agents the registration tracker flags as new.
	/// Every turn also emits the user/assistant Messages and any tools invoked this turn.
	/// </summary>
	/// <remarks>
	/// Token accounting matches what the model receives:
	/// <list type="bullet">
	///   <item>Skills carry their own <c>Instructions</c> tokens.</item>
	///   <item>System prompt tokens = est(merged instruction) − Σ est(skill.Instructions)
	///   so System + Skills sums equal the full system message size without double-counting.</item>
	///   <item>Tools/MCP tools are sized from their JSON schema (when available) plus a
	///   floor of <c>est(name + description)</c> so a schemaless tool still has signal.</item>
	/// </list>
	/// </remarks>
	private (IReadOnlyList<LoadedItem> Items, IReadOnlyList<LoadedItemBody> Bodies) BuildTurnLoadedItems(
		string conversationId,
		AgentDefinition? agentDef,
		string userMessage,
		string assistantResponse,
		IReadOnlyList<string> toolsInvoked)
	{
		var items = new List<LoadedItem>(8 + toolsInvoked.Count);
		// Bodies are sparse — only registration items (system / skills / tools /
		// mcp / sub-agents) carry body text. Messages get their full text via
		// the separate /messages/:messageId endpoint, so they're skipped here.
		var bodies = new List<LoadedItemBody>(8);

		var ctx = _agentCache.TryGetContext(conversationId);
		if (ctx is not null)
		{
			var snapshot = BuildRegistrationSnapshot(ctx, agentDef);
			var delta = _registrationTracker.DiffAndUpdate(conversationId, snapshot);
			AppendRegistrationItems(items, bodies, snapshot, delta);
		}

		// Always emit messages — those are the per-turn delta the inspector itemizes.
		items.Add(new LoadedItem(
			What: "User message",
			Tokens: TokenEstimationHelper.EstimateTokens(userMessage),
			Category: ContextCategory.Messages,
			Reference: null));
		items.Add(new LoadedItem(
			What: "Assistant message",
			Tokens: TokenEstimationHelper.EstimateTokens(assistantResponse),
			Category: ContextCategory.Messages,
			Reference: null));

		foreach (var toolName in toolsInvoked)
		{
			items.Add(new LoadedItem(
				What: $"Tool: {toolName}",
				Tokens: 0,
				Category: ContextCategory.Messages,
				Reference: toolName));
		}

		return (items, bodies);
	}

	/// <summary>
	/// Projects <see cref="AgentExecutionContext"/> + <see cref="AgentDefinition"/> into the
	/// shape the tracker diffs against. Splits the agent's tool list into native vs MCP
	/// using <c>AgentExecutionContext.McpToolNames</c>; resolves skill instructions from
	/// the registry so per-skill token sizing is accurate.
	/// </summary>
	private RegistrationSnapshot BuildRegistrationSnapshot(
		AgentExecutionContext ctx,
		AgentDefinition? agentDef)
	{
		var skills = new List<SkillRegistration>();
		if (ctx.SkillIds is not null)
		{
			foreach (var id in ctx.SkillIds)
			{
				var skill = _skillRegistry.TryGet(id);
				if (skill is null) continue;
				skills.Add(new SkillRegistration(skill.Id, skill.Name, skill.Instructions));
			}
		}

		var mcpNames = ctx.McpToolNames ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var native = new List<ToolRegistration>();
		var mcp = new List<ToolRegistration>();
		if (ctx.Tools is not null)
		{
			foreach (var tool in ctx.Tools)
			{
				var aiFunc = tool as AIFunction;
				string? schema = aiFunc?.JsonSchema.ToString();
				var reg = new ToolRegistration(tool.Name, tool.Description, schema);
				if (mcpNames.Contains(tool.Name)) mcp.Add(reg);
				else native.Add(reg);
			}
		}

		// Sub-agents: only AGENT.md-discoverable peers count for the Agents lane today.
		// Self is excluded so the agent doesn't show up as a delegation target on itself.
		var subAgents = new List<AgentRegistration>();
		if (agentDef is not null)
		{
			foreach (var peer in _agentRegistry.GetAll())
			{
				if (string.Equals(peer.Id, agentDef.Id, StringComparison.OrdinalIgnoreCase)) continue;
				subAgents.Add(new AgentRegistration(peer.Id, peer.Name, peer.Description));
			}
		}

		return new RegistrationSnapshot(
			SystemPromptText: ctx.Instruction,
			Skills: skills,
			NativeTools: native,
			McpTools: mcp,
			SubAgents: subAgents);
	}

	/// <summary>
	/// Emits one <see cref="LoadedItem"/> per registration delta entry into <paramref name="items"/>
	/// and, in lockstep, one <see cref="LoadedItemBody"/> per item into <paramref name="bodies"/>.
	/// System tokens = est(instruction) − Σ est(skill.Instructions) so the lane totals add up
	/// to what the model actually receives without double-counting skill content.
	/// Body capture pairs each LoadedItem with its actual text (composed system prompt, skill
	/// instructions, tool JSON schema, MCP descriptor, sub-agent description) so the dashboard
	/// drawer can render the real content via the lazy
	/// <c>GET /sessions/:id/turns/:turn/loaded/:idx/body</c> endpoint.
	/// </summary>
	private static void AppendRegistrationItems(
		List<LoadedItem> items,
		List<LoadedItemBody> bodies,
		RegistrationSnapshot snapshot,
		RegistrationDelta delta)
	{
		if (delta.SystemPromptIsNew && !string.IsNullOrEmpty(snapshot.SystemPromptText))
		{
			var instructionTokens = TokenEstimationHelper.EstimateTokens(snapshot.SystemPromptText);
			var skillTokens = snapshot.Skills.Sum(s =>
				string.IsNullOrEmpty(s.InstructionsText) ? 0 : TokenEstimationHelper.EstimateTokens(s.InstructionsText));
			items.Add(new LoadedItem(
				What: "System prompt",
				Tokens: Math.Max(0, instructionTokens - skillTokens),
				Category: ContextCategory.System,
				Reference: null));
			bodies.Add(new LoadedItemBody(items.Count - 1, snapshot.SystemPromptText));
		}

		foreach (var skill in delta.NewSkills)
		{
			var tokens = string.IsNullOrEmpty(skill.InstructionsText)
				? 0
				: TokenEstimationHelper.EstimateTokens(skill.InstructionsText);
			items.Add(new LoadedItem(
				What: $"Skill: {skill.Name}",
				Tokens: tokens,
				Category: ContextCategory.Skills,
				Reference: skill.Id));
			if (!string.IsNullOrEmpty(skill.InstructionsText))
				bodies.Add(new LoadedItemBody(items.Count - 1, skill.InstructionsText));
		}

		foreach (var tool in delta.NewNativeTools)
		{
			items.Add(new LoadedItem(
				What: $"Tool: {tool.Name}",
				Tokens: EstimateToolTokens(tool),
				Category: ContextCategory.Tools,
				Reference: tool.Name));
			var body = BuildToolBody(tool);
			if (!string.IsNullOrEmpty(body))
				bodies.Add(new LoadedItemBody(items.Count - 1, body));
		}

		foreach (var tool in delta.NewMcpTools)
		{
			items.Add(new LoadedItem(
				What: $"MCP: {tool.Name}",
				Tokens: EstimateToolTokens(tool),
				Category: ContextCategory.Mcp,
				Reference: tool.Name));
			var body = BuildToolBody(tool);
			if (!string.IsNullOrEmpty(body))
				bodies.Add(new LoadedItemBody(items.Count - 1, body));
		}

		foreach (var peer in delta.NewSubAgents)
		{
			var tokens = string.IsNullOrEmpty(peer.Description)
				? 0
				: TokenEstimationHelper.EstimateTokens(peer.Description);
			items.Add(new LoadedItem(
				What: $"Agent: {peer.Name}",
				Tokens: tokens,
				Category: ContextCategory.Agents,
				Reference: peer.Id));
			if (!string.IsNullOrEmpty(peer.Description))
				bodies.Add(new LoadedItemBody(items.Count - 1, peer.Description));
		}
	}

	/// <summary>
	/// Builds the drawer body text for a tool / MCP-tool registration. Prefers
	/// the JSON schema (what the LLM actually sees) and falls back to a "Name —
	/// Description" line so a tool without a serialised schema still has
	/// something readable to render.
	/// </summary>
	private static string BuildToolBody(ToolRegistration tool)
	{
		if (!string.IsNullOrEmpty(tool.SchemaText)) return tool.SchemaText;
		if (!string.IsNullOrEmpty(tool.Description)) return $"{tool.Name} — {tool.Description}";
		return tool.Name;
	}

	private static int EstimateToolTokens(ToolRegistration tool)
	{
		// Schema text dominates when present; fall back to name + description so a
		// tool without a serialised schema (e.g. a non-AIFunction tool) still
		// reports a non-zero footprint.
		if (!string.IsNullOrEmpty(tool.SchemaText))
			return TokenEstimationHelper.EstimateTokens(tool.SchemaText);
		var fallback = (tool.Name ?? string.Empty) + " " + (tool.Description ?? string.Empty);
		return TokenEstimationHelper.EstimateTokens(fallback);
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
