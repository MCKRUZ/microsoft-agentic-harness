using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Categorization;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Governance;
using Domain.AI.Agents;
using Domain.AI.Context;
using Domain.AI.Governance;
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
	private readonly IToolCallAdmissionPipeline _admissionPipeline;
	private readonly IAgentMetadataRegistry _agentRegistry;
	private readonly ISkillMetadataRegistry _skillRegistry;
	private readonly IConversationRegistrationTracker _registrationTracker;
	private readonly IObservabilityStore _observabilityStore;
	private readonly ILlmUsageCapture _usageCapture;
	private readonly IContextSnapshotComputer _snapshotComputer;
	private readonly IContextSnapshotNotifier _snapshotNotifier;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger<ExecuteAgentTurnCommandHandler> _logger;
	private readonly ISecretRedactor? _redactor;
	private readonly IToolCallReplayTreatment _toolCallReplayTreatment;

	public ExecuteAgentTurnCommandHandler(
		IAgentConversationCache agentCache,
		IToolCallAdmissionPipeline admissionPipeline,
		IAgentMetadataRegistry agentRegistry,
		ISkillMetadataRegistry skillRegistry,
		IConversationRegistrationTracker registrationTracker,
		IObservabilityStore observabilityStore,
		ILlmUsageCapture usageCapture,
		IContextSnapshotComputer snapshotComputer,
		IContextSnapshotNotifier snapshotNotifier,
		TimeProvider timeProvider,
		ILogger<ExecuteAgentTurnCommandHandler> logger,
		IToolCallReplayTreatment toolCallReplayTreatment,
		ISecretRedactor? redactor = null)
	{
		_agentCache = agentCache;
		_admissionPipeline = admissionPipeline;
		_agentRegistry = agentRegistry;
		_skillRegistry = skillRegistry;
		_registrationTracker = registrationTracker;
		_observabilityStore = observabilityStore;
		_usageCapture = usageCapture;
		_snapshotComputer = snapshotComputer;
		_snapshotNotifier = snapshotNotifier;
		_timeProvider = timeProvider;
		_logger = logger;
		_toolCallReplayTreatment = toolCallReplayTreatment;
		_redactor = redactor;
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
					AgentInstructions = agentDef?.Instructions,
					AllowedTools = agentDef?.AllowedTools,
					OwningAgentId = agentDef?.Id,
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

			// Same bridge for tool admission: the agent (and its cached tool functions) outlive this
			// scope, so expose this turn's scoped admission chain ambiently for the governed tool
			// wrapper to consult at invocation time. Reset first: nested MediatR sends within a
			// conversation share one scope (one chain), so clear prior turns' governance decisions and
			// loop-guard call history so this turn reflects only this turn — mirrors the _usageCapture
			// clear above. One reset covers every stateful stage; there is no longer a second one to
			// forget.
			//
			// Ordered BEFORE the two ambient assignments below, and that ordering is load-bearing:
			// only the try/finally further down clears them, so anything that can throw between an
			// assignment and that try would leave a stale scope armed on this async flow — applying
			// one turn's state to a later, unrelated turn. Reset() is the only throwing statement in
			// that window, so hoisting it above the assignments closes the window rather than
			// narrowing it.
			_admissionPipeline.Reset();

			// Set ambient capture so the singleton-scoped ObservabilityMiddleware
			// records to this handler's scoped ILlmUsageCapture instance.
			LlmUsageCapture.Current = _usageCapture;

			// Snapshot which tool-call ids are already present in the seed history — a replayed
			// conversation's FunctionResultContent is indistinguishable, by content alone, from one
			// this turn is about to produce for real. ToolDiagnosticsMiddleware consults this ambient
			// scope to skip re-recording the former while still recording the latter. Seeded once,
			// before dispatch, then grown in place by that middleware: see ReplayedToolCallScope's
			// remarks for why the seed is taken here rather than re-derived mid-turn.
			ReplayedToolCallScope.Current = new ReplayedToolCallSet(
				messages
					.SelectMany(m => m.Contents)
					.OfType<FunctionResultContent>()
					.Select(r => r.CallId));

			object? response;
			IReadOnlyList<ToolExchange> toolExchanges;
			var turnSw = Stopwatch.StartNew();

			// Begin rather than assign-and-null: nulling on teardown would disarm whatever an
			// enclosing flow had armed, leaving the outer call ungoverned for the rest of its life.
			using (ToolAdmissionAccessor.Begin(_admissionPipeline))
			{
				try
				{
					// When a transport has attached a streaming sink, stream assistant text
					// deltas as the model generates them (real perceived-latency win). Usage
					// and tool capture still flow through the chat-client middleware, so the
					// post-turn accounting below is identical to the blocking path. With no
					// sink (tests, batch callers) fall back to a single blocking call.
					var streamSink = AgentTurnStreamSink.Current;
					if (streamSink is not null)
					{
						var (text, exchanges) = await RunStreamingTurnAsync(
							agent, messages, streamSink, _redactor, _logger, cancellationToken);
						response = text;
						toolExchanges = exchanges;
					}
					else
					{
						var agentResponse = await agent.RunAsync(messages, cancellationToken: cancellationToken);
						response = agentResponse;
						toolExchanges = ToolCallTranscriptExtractor.Extract(agentResponse, _logger);
					}

					turnSw.Stop();
				}
				finally
				{
					LlmUsageCapture.Current = null;
					ReplayedToolCallScope.Current = null;
				}
			}

			// Capture accumulated token usage from all LLM calls during this turn
			var usage = _usageCapture.TakeSnapshot();

			// Extract response text; tool names come from the ambient capture
			var responseText = ExtractResponseText(response);
			var toolsInvoked = usage.ToolNames;
			var toolCalls = BuildTreatedToolCallRecords(toolExchanges);

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
				var (turnLoaded, turnLoadedBodies, registrations) = BuildTurnLoadedItems(
					request.ConversationId,
					agentDef,
					request.UserMessage,
					responseText,
					toolsInvoked);
				var snapshot = _snapshotComputer.Compute(
					conversationId: request.ConversationId,
					turnIndex: request.TurnNumber,
					turnId: $"t-{request.TurnNumber:D2}",
					history: updatedHistory,
					registrations: registrations,
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
				ToolCalls = toolCalls,
				InputTokens = usage.InputTokens,
				OutputTokens = usage.OutputTokens,
				CacheRead = usage.CacheRead,
				CacheWrite = usage.CacheWrite,
				CostUsd = usage.CostUsd,
				Model = usage.Model,
				Governance = _admissionPipeline.GetTrace()
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
	/// Also forwards tool-call activity — <see cref="FunctionCallContent"/> (the model's decision to
	/// call a tool) and <see cref="FunctionResultContent"/> (the tool's output, once
	/// <c>FunctionInvokingChatClient</c> has actually invoked it) both arrive as part of the same
	/// <c>update.Contents</c> stream. Both are redacted via <see cref="ToolPayloadRedactor"/> before
	/// reaching the sink — the same treatment <c>ToolDiagnosticsMiddleware</c> applies before the
	/// identical data is persisted, since a live SSE stream is just as much an exposure point for
	/// secrets as the observability store is. Neither is ever truncated — truncating mid-JSON would
	/// silently hand a client invalid, unparseable data for arguments, and a truncated result past
	/// <see cref="ToolPayloadRedactor.MaxStructuralRedactionCeiling"/> could still contain an
	/// unredacted secret — so above <see cref="ToolPayloadRedactor.MaxStreamedToolCallPayloadLength"/>
	/// both are withheld whole instead (<see cref="StreamedToolCallArguments.Withheld"/>,
	/// <see cref="StreamedToolCallResult.Withheld"/>), since a size cap and a truncation cap are not
	/// the same thing. When the tool failed, a generic message is streamed instead of
	/// <see cref="FunctionResultContent.Result"/>'s raw text, since <c>IncludeDetailedErrors</c> bakes
	/// the exception message into that string (see <see cref="RedactedResultForStreaming"/>). Usage and
	/// tool-call capture still flow through the chat-client middleware, so the caller's post-turn
	/// accounting is unchanged. The same <paramref name="cancellationToken"/> flows to
	/// <c>RunStreamingAsync</c>, so a disconnected consumer aborts the model call.
	/// </summary>
	/// <remarks>
	/// Also returns this turn's tool exchanges for #249 item 6's replay memory. The non-streaming path
	/// extracts these from <see cref="AgentResponse.Messages"/> after the call returns; a streaming run
	/// never produces that aggregate object, so this method accumulates the same
	/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> pairs itself as they arrive
	/// in <c>update.Contents</c> and feeds them through the identical
	/// <see cref="ToolCallTranscriptExtractor"/> pairing logic — one extraction algorithm, two content
	/// sources, so the two paths cannot silently diverge on how a call is paired to its result.
	/// </remarks>
	private static async Task<(string Text, IReadOnlyList<ToolExchange> ToolExchanges)> RunStreamingTurnAsync(
		AIAgent agent,
		IReadOnlyList<ChatMessage> messages,
		IAgentTurnStreamSink sink,
		ISecretRedactor? redactor,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var builder = new StringBuilder();
		var capturedContents = new List<AIContent>();
		// Wraps the ambient sink for the lifetime of this one turn only — never held past this method
		// or reused across turns. See ToolCallOrderingSink's remarks for why per-turn construction
		// (not, say, wrapping once per run) is load-bearing: some providers reuse simple call ids
		// across turns, and a wrapper that lived longer than one turn would misidentify a reused id as
		// a duplicate.
		var orderedSink = new ToolCallOrderingSink(sink);
		await foreach (var update in agent.RunStreamingAsync(messages, cancellationToken: cancellationToken))
		{
			var delta = update.Text;
			if (!string.IsNullOrEmpty(delta))
			{
				builder.Append(delta);
				await orderedSink.EmitAsync(delta, cancellationToken);
			}

			foreach (var content in update.Contents)
			{
				await EmitToolCallActivityAsync(content, orderedSink, redactor, logger, cancellationToken);

				if (content is FunctionCallContent or FunctionResultContent)
					capturedContents.Add(content);
			}
		}

		var toolExchanges = capturedContents.Count == 0
			? []
			: ToolCallTranscriptExtractor.Extract(
				[new ChatMessage(ChatRole.Assistant, capturedContents)], logger);

		return (builder.ToString(), toolExchanges);
	}

	/// <summary>
	/// Handles one item from a streaming update's <c>Contents</c> — a tool-call decision or a tool's
	/// result — forwarding it to <paramref name="sink"/> when it's one of the two content types this
	/// stream cares about. Extracted from <see cref="RunStreamingTurnAsync"/> to keep that method under
	/// the 50-line convention. <paramref name="sink"/> is expected to be a
	/// <see cref="ToolCallOrderingSink"/> (or another sink honoring the same ordering contract), so this
	/// method applies only the semantic guards a well-formed frame requires — a non-empty
	/// <c>CallId</c>, and a non-empty <c>Name</c> for the call side — and leaves duplicate-start and
	/// orphaned-result enforcement to the sink.
	/// </summary>
	private static async Task EmitToolCallActivityAsync(
		AIContent content,
		IAgentTurnStreamSink sink,
		ISecretRedactor? redactor,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		switch (content)
		{
			// Guards on both CallId and Name: a call with no id can't be matched to its later
			// result (an empty toolCallId would violate the wire contract's required field), and
			// a call with no name has nothing meaningful to announce.
			case FunctionCallContent { CallId.Length: > 0 } call when !string.IsNullOrEmpty(call.Name):
				await sink.EmitToolCallAsync(
					call.CallId, call.Name, RedactedArgsJson(call, redactor, logger), cancellationToken);
				break;

			// A non-empty CallId is the only guard needed here — the sink itself drops a result with
			// no matching preceding TOOL_CALL_START (e.g. a call skipped above for an empty Name).
			case FunctionResultContent { CallId.Length: > 0 } result:
				await sink.EmitToolCallResultAsync(
					result.CallId, RedactedResultForStreaming(result, redactor, logger), cancellationToken);
				break;
		}
	}

	/// <summary>
	/// Serializes and redacts a tool call's arguments for streaming. An unserializable argument value
	/// (an unsupported CLR type from a poorly-behaved tool) must degrade to a withheld result, not
	/// abort the whole turn — text already streamed to the client before this tool call must not be
	/// thrown away over it. Redaction failure and the size ceiling are both handled by
	/// <see cref="ToolPayloadRedactor.RedactForStreaming"/>, which this method shares with
	/// <c>AgUiClientToolBridge</c>'s identical need on the AG-UI client round-trip transport.
	/// Deliberately independent of <c>ToolDiagnosticsMiddleware.LogToolCallsInResponse</c>, which
	/// redacts the same <see cref="FunctionCallContent.Arguments"/> a second time for the persisted
	/// observability record (#389, investigated and closed as won't-fix) — see that method's comment
	/// for why.
	/// </summary>
	private static StreamedToolCallArguments RedactedArgsJson(FunctionCallContent call, ISecretRedactor? redactor, ILogger logger)
	{
		if (call.Arguments is not { Count: > 0 } args)
			return new StreamedToolCallArguments("{}", Withheld: false);

		string serialized;
		try
		{
			serialized = JsonSerializer.Serialize(args);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex,
				"Failed to serialize streamed tool-call arguments for {ToolName} CallId={CallId}",
				call.Name, call.CallId);
			return new StreamedToolCallArguments("{}", Withheld: true);
		}

		return ToolPayloadRedactor.RedactForStreaming(serialized, redactor, logger, call.Name, call.CallId);
	}

	/// <summary>
	/// Builds a safe, streamable representation of a tool's result — the result-path counterpart to
	/// <see cref="RedactedArgsJson"/>, sharing the same withhold-above-a-ceiling treatment
	/// <see cref="ToolPayloadRedactor.RedactResultForStreaming"/> applies instead of the fixed-length
	/// truncation this method used before (#417): a truncated preview past
	/// <see cref="ToolPayloadRedactor.MaxStructuralRedactionCeiling"/> could still contain an
	/// unredacted secret (<c>PatternSecretRedactor</c> falls back to a regex-only pass above that
	/// size, which cannot see through the escaped-nested-JSON secret shape #391 closed for smaller
	/// payloads), and a client parsing a streamed result as structured data — not just a human-read
	/// preview — could receive truncated, invalid data either way. No separate 64KB pre-check is
	/// needed here: <see cref="ToolPayloadRedactor.MaxStreamedToolCallPayloadLength"/> (16KB) is
	/// already below <see cref="ToolPayloadRedactor.MaxStructuralRedactionCeiling"/> (64KB), so
	/// <c>RedactResultForStreaming</c>'s own ceiling always withholds first.
	/// <see cref="ToolPayloadRedactor.SafeResultText"/> substitutes a generic message when the call
	/// failed, since <c>FunctionInvokingChatClient</c>'s <c>IncludeDetailedErrors</c> option (set
	/// unconditionally by <c>AgentFactory</c>) bakes the raw exception message into
	/// <see cref="FunctionResultContent.Result"/> — the same substitution <c>ToolDiagnosticsMiddleware</c>
	/// applies before persisting to the trace store a dashboard later renders, since that is just as
	/// much an exposure point as this client-facing SSE frame.
	/// </summary>
	private static StreamedToolCallResult RedactedResultForStreaming(FunctionResultContent result, ISecretRedactor? redactor, ILogger logger) =>
		ToolPayloadRedactor.RedactResultForStreaming(ToolPayloadRedactor.SafeResultText(result), redactor, logger, result.CallId);

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
	private (IReadOnlyList<LoadedItem> Items, IReadOnlyList<LoadedItemBody> Bodies, CategoryBreakdown Registrations)
		BuildTurnLoadedItems(
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

		// Cumulative registration totals for the context bar, alongside the per-turn delta items for
		// the inspector drawer. Both are derived from the same RegistrationSnapshot and the same
		// per-item arithmetic (RegistrationBreakdownCalculator), so the bar and the drawer cannot
		// disagree about the turn they are both describing — they answer different questions
		// (running state vs. what changed) from one measurement.
		//
		// Empty when no agent context is resolvable: nothing is known to be registered, so nothing is
		// claimed. The whole prompt then lands in ContextSnapshot.UnaccountedTokens, which is the
		// honest reading — unattributed, not absent.
		var registrations = CategoryBreakdown.Empty;

		var ctx = _agentCache.TryGetContext(conversationId);
		if (ctx is not null)
		{
			var snapshot = BuildRegistrationSnapshot(ctx, agentDef);
			registrations = RegistrationBreakdownCalculator.From(snapshot);
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

		return (items, bodies, registrations);
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
		// A skill being in scope does not mean its body is in the prompt: the framework serves Tier-2
		// bodies on demand by default, and the merge that built ctx.Instruction deliberately omits them.
		// Carrying that distinction is what stops the context bar sizing the system prompt as
		// "instruction minus every registered skill" and subtracting text that was never there (#507).
		var disclosedOnDemand = ctx.DisclosedOnDemandSkillIds;
		var skills = new List<SkillRegistration>();
		if (ctx.SkillIds is not null)
		{
			foreach (var id in ctx.SkillIds)
			{
				var skill = _skillRegistry.TryGet(id);
				if (skill is null) continue;
				skills.Add(new SkillRegistration(
					skill.Id,
					skill.Name,
					skill.Instructions,
					InlinedInPrompt: disclosedOnDemand?.Contains(skill.Id) != true));
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
			items.Add(new LoadedItem(
				What: "System prompt",
				Tokens: RegistrationBreakdownCalculator.SystemPromptTokens(snapshot),
				Category: ContextCategory.System,
				Reference: null));
			bodies.Add(new LoadedItemBody(items.Count - 1, snapshot.SystemPromptText));
		}

		foreach (var skill in delta.NewSkills)
		{
			items.Add(new LoadedItem(
				What: $"Skill: {skill.Name}",
				Tokens: RegistrationBreakdownCalculator.TokensFor(skill),
				Category: ContextCategory.Skills,
				Reference: skill.Id));
			if (!string.IsNullOrEmpty(skill.InstructionsText))
				bodies.Add(new LoadedItemBody(items.Count - 1, skill.InstructionsText));
		}

		foreach (var tool in delta.NewNativeTools)
		{
			items.Add(new LoadedItem(
				What: $"Tool: {tool.Name}",
				Tokens: RegistrationBreakdownCalculator.TokensFor(tool),
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
				Tokens: RegistrationBreakdownCalculator.TokensFor(tool),
				Category: ContextCategory.Mcp,
				Reference: tool.Name));
			var body = BuildToolBody(tool);
			if (!string.IsNullOrEmpty(body))
				bodies.Add(new LoadedItemBody(items.Count - 1, body));
		}

		foreach (var peer in delta.NewSubAgents)
		{
			items.Add(new LoadedItem(
				What: $"Agent: {peer.Name}",
				Tokens: RegistrationBreakdownCalculator.TokensFor(peer),
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

	/// <summary>
	/// Treats each extracted tool exchange for durable, model-facing replay and builds the persisted
	/// record for it (#249 item 6).
	/// </summary>
	/// <remarks>
	/// Enforces the severity-one invariant structurally rather than by caller discipline: every record
	/// this method returns has a non-null <c>ToolCallRecord.Output</c>, substituting
	/// <see cref="IToolCallReplayTreatment.NoResultPlaceholder"/> for an orphaned call
	/// (<see cref="ToolExchange.HasResult"/> false) rather than persisting one with no result — a
	/// persisted assistant <c>tool_calls</c> message with no matching result message is a malformed
	/// conversation a provider rejects, permanently, on every subsequent turn.
	/// <para>
	/// <c>ToolCallRecord.DurationMs</c> is set to <c>0</c> — no per-call wall-clock timer feeds
	/// this path today, matching the same placeholder <c>ApproveChangeProposalCommandHandler</c> and its
	/// siblings already use where a real duration isn't tracked. Adding one is out of scope for a
	/// replay-memory feature.
	/// </para>
	/// <para>
	/// Checks <see cref="IToolCallReplayTreatment.Enabled"/> first and returns empty when a deployment
	/// has opted the feature off — the check has to live here, before any treatment work happens, not
	/// after: treating a payload and then discarding it would still have run the sanitize/redact pass
	/// this method exists to gate, and would leave a caller-visible difference between "opted out" and
	/// "genuinely made no tool calls" only in the log, never in what gets persisted.
	/// </para>
	/// <para>
	/// Caps the turn at <see cref="IToolCallReplayTreatment.MaxCallsPerTurn"/> records for the same
	/// before-any-work reason, keeping the newest and logging how many it dropped. This is the
	/// write-side half of the bound; it cannot be the whole of it, because it does nothing for rows
	/// persisted before the cap existed — <c>ConversationMessageMapping.ToChatMessages</c> enforces the
	/// read-side character budget that covers those, and trims from the same end so the two compose
	/// into one coherent policy rather than a middle slice.
	/// </para>
	/// </remarks>
	private IReadOnlyList<ToolCallRecord> BuildTreatedToolCallRecords(IReadOnlyList<ToolExchange> exchanges)
	{
		if (exchanges.Count == 0 || !_toolCallReplayTreatment.Enabled)
			return [];

		// Read once into a local, not four times off IOptionsMonitor.CurrentValue: a hot config reload
		// between the comparison and the Take would apply one limit while reporting another, or skip the
		// cap entirely. Same reason DurableTranscript snapshots its own budget at construction — half a
		// policy applied mid-decision is worse than either whole one.
		var maxCallsPerTurn = _toolCallReplayTreatment.MaxCallsPerTurn;

		// Bound what one turn persists before treating anything, for the same reason the Enabled check
		// above comes first: treating a payload and then discarding it would still have run the
		// sanitize/redact pass this cap exists to avoid paying for. Nothing upstream bounds this — the
		// framework's per-request iteration limit caps tool-calling ROUNDS, while the chat client is
		// built with concurrent invocation allowed, so one round's parallel calls are unbounded.
		//
		// Newest-first, matching ConversationMessageMapping's read-side budget exactly. The two halves
		// of one policy must trim from the same end: keeping the earliest here and the newest there
		// would compose into a middle slice — neither the beginning of the turn's reasoning nor its
		// conclusions — and would make the read side's contiguous-newest-tail guarantee false of the
		// system even though it holds of that method. Newest is the right shared direction because this
		// is a memory, not an audit log: a later turn reasons about what the agent last found, and the
		// assistant prose persisted beside these records summarizes the outcome rather than the opening
		// moves.
		var admitted = exchanges;
		if (exchanges.Count > maxCallsPerTurn)
		{
			var dropped = exchanges.Count - maxCallsPerTurn;
			_logger.LogWarning(
				"[ToolCallReplay] Turn produced {Total} tool calls, above the {Cap} persisted per turn; " +
				"dropping the {Dropped} earliest from replay history.",
				exchanges.Count, maxCallsPerTurn, dropped);

			admitted = exchanges
				.OrderBy(e => e.RoundOrdinal)
				.TakeLast(maxCallsPerTurn)
				.ToList();
		}

		var records = new List<ToolCallRecord>(admitted.Count);
		foreach (var exchange in admitted)
		{
			var treatedInput = string.IsNullOrEmpty(exchange.ArgsJson)
				? null
				: _toolCallReplayTreatment.Treat(exchange.ArgsJson, exchange.ToolName);

			var treatedOutput = exchange.HasResult
				? _toolCallReplayTreatment.Treat(exchange.ResultText ?? string.Empty, exchange.ToolName)
				: _toolCallReplayTreatment.NoResultPlaceholder;

			records.Add(new ToolCallRecord(
				exchange.ToolName,
				treatedInput,
				treatedOutput,
				DurationMs: 0,
				CallId: exchange.CallId,
				RoundOrdinal: exchange.RoundOrdinal));
		}

		return records;
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
