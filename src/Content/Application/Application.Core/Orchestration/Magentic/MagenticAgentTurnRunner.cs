using System.Collections.Concurrent;
using System.Text;
using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Traces;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.Core.Orchestration.Magentic;

/// <summary>
/// Default <see cref="IMagenticAgentTurnRunner"/>. See that interface for the design rationale.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Known v1 limitation — no per-tool-call replay records.</strong> A single-agent turn
/// extracts <see cref="AgentTurnResult.ToolCalls"/> from the one <c>AgentResponse</c> its own
/// <c>AIAgent.RunAsync</c> call produced. A Magentic workflow drives an unknown number of separate
/// <c>RunAsync</c> calls across the manager and every participant, internally, via MAF's own
/// coordination loop — there is no single response object to extract a transcript from. This
/// runner always returns an empty <see cref="AgentTurnResult.ToolCalls"/>; tool *names* still
/// surface via <see cref="AgentTurnResult.ToolsInvoked"/> (from the shared ambient usage capture),
/// but per-call replay memory (#249 item 6) does not yet cover Magentic turns. Revisit if/when a
/// Magentic turn needs to participate in tool-call replay history.
/// </para>
/// <para>
/// <strong>Known v1 limitation — no per-skill effectiveness attribution.</strong> A Magentic turn's
/// success or failure is a property of the whole workflow (manager plus every participant), not of
/// one agent's one skill set the way a single-agent turn's is. <see cref="AgentTurnResult.SkillIds"/>
/// (#695) is always empty here rather than the union of every participant's skills: a workflow
/// failure caused by one misbehaving participant would otherwise get spread across every other
/// participant's skills too, penalizing skills that behaved correctly. Revisit only with a real
/// per-participant success signal, not by flattening the workflow outcome onto every skill involved.
/// </para>
/// </remarks>
public sealed class MagenticAgentTurnRunner : IMagenticAgentTurnRunner
{
	private readonly IAgentFactory _agentFactory;
	private readonly IAgentMetadataRegistry _agentRegistry;
	private readonly IMagenticOrchestrator _orchestrator;
	private readonly ILlmUsageCapture _usageCapture;
	private readonly IToolCallAdmissionPipeline _admissionPipeline;
	private readonly ILogger<MagenticAgentTurnRunner> _logger;

	public MagenticAgentTurnRunner(
		IAgentFactory agentFactory,
		IAgentMetadataRegistry agentRegistry,
		IMagenticOrchestrator orchestrator,
		ILlmUsageCapture usageCapture,
		IToolCallAdmissionPipeline admissionPipeline,
		ILogger<MagenticAgentTurnRunner> logger)
	{
		ArgumentNullException.ThrowIfNull(agentFactory);
		ArgumentNullException.ThrowIfNull(agentRegistry);
		ArgumentNullException.ThrowIfNull(orchestrator);
		ArgumentNullException.ThrowIfNull(usageCapture);
		ArgumentNullException.ThrowIfNull(admissionPipeline);
		ArgumentNullException.ThrowIfNull(logger);

		_agentFactory = agentFactory;
		_agentRegistry = agentRegistry;
		_orchestrator = orchestrator;
		_usageCapture = usageCapture;
		_admissionPipeline = admissionPipeline;
		_logger = logger;
	}

	/// <inheritdoc/>
	public async Task<AgentTurnResult> RunTurnAsync(
		AgentDefinition supervisor,
		string conversationId,
		string userMessage,
		IReadOnlyList<ChatMessage> conversationHistory,
		MagenticTurnOverrides overrides,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(overrides);
		ArgumentNullException.ThrowIfNull(supervisor);
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
		ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
		ArgumentNullException.ThrowIfNull(conversationHistory);

		if (supervisor.Participants.Count == 0)
		{
			_logger.LogError(
				"Supervisor agent {AgentId} declares orchestration: magentic but has no participants",
				supervisor.Id);

			return Failure(userMessage, conversationHistory,
				"This agent is configured as a Magentic supervisor but declares no participants.");
		}

		_logger.LogInformation(
			"Running Magentic turn for supervisor {AgentId} with {ParticipantCount} participant(s)",
			supervisor.Id, supervisor.Participants.Count);

		// Collects every agent-execution context this turn builds (manager and every participant,
		// including ones whose sibling build failed) so their execution-trace writers — opened by
		// AgentExecutionContextFactory.StartTraceRunAsync when MetaHarness.ExecutionTracingEnabled is
		// on — are always finalized and disposed, no matter how this method exits. The single-agent
		// path gets this for free from IAgentConversationCache's eviction callback
		// (ExecutionTraceWriterCleanup, extracted from there); this runner builds agents directly
		// through IAgentFactory instead and so must finalize them itself, immediately, since these
		// agents are never cached or reused past this one turn anyway.
		var builtContexts = new ConcurrentBag<AgentExecutionContext>();
		try
		{
			return await RunTurnCoreAsync(
				supervisor, conversationId, userMessage, conversationHistory, overrides, builtContexts,
				cancellationToken);
		}
		finally
		{
			foreach (var context in builtContexts)
				await ExecutionTraceWriterCleanup.CompleteAsync(context, _logger, CancellationToken.None);
		}
	}

	/// <summary>
	/// The actual turn logic, split out of <see cref="RunTurnAsync"/> only so that method's
	/// trace-writer-cleanup <c>finally</c> can wrap every exit path (including the early
	/// no-participants-declared return, which never reaches here) without the wrapper itself growing
	/// past the point of being one function's worth of responsibility.
	/// </summary>
	private async Task<AgentTurnResult> RunTurnCoreAsync(
		AgentDefinition supervisor,
		string conversationId,
		string userMessage,
		IReadOnlyList<ChatMessage> conversationHistory,
		MagenticTurnOverrides overrides,
		ConcurrentBag<AgentExecutionContext> builtContexts,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<AIAgent> participants;
		AIAgent manager;
		try
		{
			(manager, participants) = await BuildManagerAndParticipantsAsync(
				supervisor, conversationId, overrides, builtContexts, cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Anything can fail here — most notably, an agent (manager or participant) whose skills
			// declare prerequisites throws if the prerequisite-tracking scope isn't wired correctly.
			// A build failure is exactly as much "this turn can't proceed" as an unresolvable
			// participant is, so it gets the same graceful Failure rather than propagating raw and
			// aborting the whole handler ungracefully.
			_logger.LogError(ex,
				"Supervisor {AgentId} failed to build its manager or a participant agent", supervisor.Id);

			return Failure(userMessage, conversationHistory,
				"This Magentic supervisor's agents could not be constructed.");
		}

		if (participants.Count == 0)
		{
			return Failure(userMessage, conversationHistory,
				"None of this Magentic supervisor's declared participants could be resolved.");
		}

		var task = BuildTask(userMessage, conversationHistory);
		var options = supervisor.MagenticOptions;

		var request = new MagenticWorkflowRequest
		{
			Manager = manager,
			Participants = participants,
			Task = task,
			Name = supervisor.Id,
			MaxRounds = options?.MaxRounds,
			MaxStalls = options?.MaxStalls ?? 3,
			MaxResets = options?.MaxResets,
			RequirePlanSignoff = options?.RequirePlanSignoff ?? false,
		};

		// ExecuteAgentTurnCommand.Timeout caps a live turn at 5 minutes. A HITL plan-review pause
		// (RequirePlanSignoff) can legitimately exceed that waiting on a human, and an unbounded
		// MaxRounds has no ceiling of its own to fall back on — both are unsupported combinations on
		// the live-turn path today (fine for the console/batch callers this orchestrator also serves,
		// where nothing enforces that ceiling). Warn rather than refuse: a supervisor configured this
		// way still fails safely via the surrounding command timeout, just not usefully.
		if (request.RequirePlanSignoff || request.MaxRounds is null)
		{
			_logger.LogWarning(
				"Supervisor {AgentId} sets RequirePlanSignoff={RequirePlanSignoff}, MaxRounds={MaxRounds} — " +
				"a live conversation turn is capped at 5 minutes and this configuration has no ceiling of " +
				"its own, so the turn may time out rather than complete",
				supervisor.Id, request.RequirePlanSignoff, request.MaxRounds);
		}

		// Reset() first, ambient assignment second — load-bearing ordering, not incidental. It is the
		// only statement in this window that can throw; hoisting it above the ambient assignment (and
		// the try/finally that clears it) is what closes the window instead of narrowing it, mirroring
		// ExecuteAgentTurnCommandHandler.Handle's identical ordering for the identical reason.
		_usageCapture.TakeSnapshot();
		_admissionPipeline.Reset();
		LlmUsageCapture.Current = _usageCapture;

		// Ambient, not part of the manager's own construction, because it must reach every model call
		// the workflow makes — manager and participants alike — the same as CallerTurnContextProvider
		// already does for a single-agent turn.
		CallerTurnContextScope.Current = overrides.TurnContext;

		Domain.Common.Result<MagenticWorkflowResult> result;
		try
		{
			using (ToolAdmissionAccessor.Begin(_admissionPipeline))
			{
				result = await _orchestrator.RunAsync(request, cancellationToken);
			}
		}
		finally
		{
			LlmUsageCapture.Current = null;
			CallerTurnContextScope.Current = null;
		}

		var usage = _usageCapture.TakeSnapshot();

		if (!result.IsSuccess)
		{
			// The workflow's own Errors can carry a raw Exception.Message rather than a stable code —
			// MagenticEventSubscriber captures error.Exception?.Message verbatim, and MagenticOrchestrator
			// wraps it as-is when no ErrorMessage was set. That text can contain internal detail (paths,
			// hostnames, a tool's exception message — AgentFactory sets IncludeDetailedErrors
			// unconditionally). The console example surfaces it because a developer reads their own
			// console; this runner is a live, callable turn whose failure reaches transports the
			// single-agent path deliberately keeps generic (ExecuteAgentTurnCommandHandler's own
			// catch-all returns a fixed string for the same reason). Log the raw detail; never return it.
			var rawError = string.Join("; ", result.Errors);
			_logger.LogError(
				"Magentic turn for supervisor {AgentId} failed: {Errors}", supervisor.Id, rawError);

			return Failure(userMessage, conversationHistory,
				"The multi-agent workflow failed to complete.");
		}

		var workflow = result.Value!;

		// A workflow that ends without synthesized text (e.g. it hit max-rounds/max-stalls before the
		// manager produced final output) but genuinely invoked tools is a complete, storable exchange,
		// not a blank one — RunConversationCommandHandler's durable-transcript gate drops a turn with
		// both empty Response and empty ToolCalls, and this runner's ToolCalls is always empty (see the
		// "known v1 limitation" above), so an empty Response here would silently vanish from the
		// transcript despite real work having happened. Same placeholder philosophy as
		// IToolCallReplayTreatment.NoResultPlaceholder: never let "nothing to show" collapse into
		// "nothing happened."
		var responseText = !string.IsNullOrWhiteSpace(workflow.FinalOutput)
			? workflow.FinalOutput!
			: usage.ToolNames.Count > 0
				? $"[The workflow used {string.Join(", ", usage.ToolNames)} but did not produce a final response.]"
				: string.Empty;

		var updatedHistory = new List<ChatMessage>(conversationHistory)
		{
			new(ChatRole.User, userMessage),
			new(ChatRole.Assistant, responseText),
		};

		return new AgentTurnResult
		{
			Success = true,
			Response = responseText,
			UpdatedHistory = updatedHistory,
			ToolsInvoked = usage.ToolNames,
			InputTokens = usage.InputTokens,
			OutputTokens = usage.OutputTokens,
			CacheRead = usage.CacheRead,
			CacheWrite = usage.CacheWrite,
			CostUsd = usage.CostUsd,
			Model = usage.Model,
			Governance = _admissionPipeline.GetTrace(),
		};
	}

	/// <summary>
	/// Resolves every declared participant to a registered <see cref="AgentDefinition"/> (skipping and
	/// warning about ones that aren't), then builds the manager and every resolved participant
	/// concurrently — each build is a genuine async round-trip (skill resolution, prerequisite checks,
	/// chat-client construction) with no data dependency on any other, so building them one at a time
	/// would pay N+1 sequential round-trips for no reason.
	/// </summary>
	/// <remarks>
	/// Deduplicates participant ids (including a participant that names the supervisor itself) before
	/// building anything — a repeated or self-referential id would otherwise hand MAF's
	/// <c>AddParticipants</c> two agent instances sharing the same name, which the framework has no
	/// defined behaviour for.
	/// </remarks>
	private async Task<(AIAgent Manager, IReadOnlyList<AIAgent> Participants)> BuildManagerAndParticipantsAsync(
		AgentDefinition supervisor, string conversationId, MagenticTurnOverrides overrides,
		ConcurrentBag<AgentExecutionContext> builtContexts, CancellationToken cancellationToken)
	{
		var seenParticipantIds = new HashSet<string>(StringComparer.Ordinal);
		var resolvedParticipantDefs = new List<AgentDefinition>(supervisor.Participants.Count);
		foreach (var participantId in supervisor.Participants)
		{
			if (string.Equals(participantId, supervisor.Id, StringComparison.Ordinal))
			{
				_logger.LogWarning(
					"Supervisor {AgentId} names itself as a participant; skipping the self-reference",
					supervisor.Id);
				continue;
			}

			if (!seenParticipantIds.Add(participantId))
			{
				_logger.LogWarning(
					"Supervisor {AgentId} names participant {ParticipantId} more than once; skipping the duplicate",
					supervisor.Id, participantId);
				continue;
			}

			var participantDef = _agentRegistry.TryGet(participantId);
			if (participantDef is null)
			{
				_logger.LogWarning(
					"Supervisor {AgentId} names participant {ParticipantId}, which is not a registered agent; skipping it",
					supervisor.Id, participantId);
				continue;
			}

			resolvedParticipantDefs.Add(participantDef);
		}

		// Overrides apply to the manager only — the agent the caller actually addressed — the same way
		// a single-agent turn's SystemPromptOverride/DeploymentOverride/Temperature apply only to the
		// one agent named on the request, never to whatever it delegates to internally.
		var managerTask = BuildAgentAsync(supervisor, conversationId, builtContexts, cancellationToken, overrides);
		var participantTasks = resolvedParticipantDefs
			.Select(def => BuildAgentAsync(def, conversationId, builtContexts, cancellationToken))
			.ToArray();
		await Task.WhenAll([managerTask, .. participantTasks]);

		return (managerTask.Result, participantTasks.Select(t => t.Result).ToList());
	}

	/// <summary>
	/// Builds an <see cref="AIAgent"/> for one agent definition — the manager or a participant —
	/// the same way <c>ExecuteAgentTurnCommandHandler</c> resolves any agent: through its own
	/// declared skills, falling back to its id as a bare skill id when it declares none.
	/// </summary>
	/// <param name="conversationId">
	/// Flowed into <see cref="AgentFactory.ConversationIdPropertyKey"/> so an agent whose skills
	/// declare prerequisites can resolve its prerequisite-tracking scope — without this,
	/// <c>AgentFactory.ResolvePrerequisiteScope</c> throws for any such agent. The single-agent path
	/// gets this for free from <c>IAgentConversationCache.GetOrCreateAsync</c>; this runner builds
	/// agents directly through <see cref="IAgentFactory"/> instead (see the "known v1 limitation" on
	/// <see cref="IMagenticAgentTurnRunner"/> about the resulting empty context-cache registration
	/// breakdown), so it sets this property itself rather than inheriting it from that cache.
	/// </param>
	/// <param name="builtContexts">
	/// Collects this build's <see cref="AgentExecutionContext"/> so <see cref="RunTurnAsync"/>'s
	/// trace-writer cleanup can finalize it regardless of whether this or a sibling concurrent build
	/// later throws. Populated as soon as this build succeeds — added before <c>Task.WhenAll</c> can
	/// possibly observe another build's failure, so a partially-failed concurrent build never leaks
	/// the contexts that DID complete.
	/// </param>
	/// <param name="overrides">
	/// Per-turn overrides to apply — pass a non-null instance only for the manager (see the call site
	/// in <see cref="RunTurnAsync"/>); a participant never receives caller-supplied overrides.
	/// </param>
	private async Task<AIAgent> BuildAgentAsync(
		AgentDefinition agentDef,
		string conversationId,
		ConcurrentBag<AgentExecutionContext> builtContexts,
		CancellationToken cancellationToken,
		MagenticTurnOverrides? overrides = null)
	{
		var skillIds = AgentDefinition.ResolveSkillIds(agentDef, agentDef.Id);

		var built = await _agentFactory.CreateAgentWithContextFromSkillsAsync(
			skillIds,
			new SkillAgentOptions
			{
				AgentInstructions = agentDef.Instructions,
				AllowedTools = agentDef.AllowedTools,
				OwningAgentId = agentDef.Id,
				AdditionalContext = overrides?.SystemPromptOverride,
				DeploymentName = overrides?.DeploymentOverride,
				Temperature = overrides?.Temperature,
				AdditionalProperties = new Dictionary<string, object>
				{
					[AgentFactory.ConversationIdPropertyKey] = conversationId,
				},
			},
			cancellationToken);

		builtContexts.Add(built.Context);
		return built.Agent;
	}

	/// <summary>
	/// Folds prior turns and the new user message into the single task string a Magentic workflow
	/// takes — see the "known v1 limitation" on <see cref="IMagenticAgentTurnRunner"/> for why this
	/// is a fresh workflow per turn rather than a resumed one.
	/// </summary>
	private static string BuildTask(string userMessage, IReadOnlyList<ChatMessage> history)
	{
		if (history.Count == 0)
			return userMessage;

		var sb = new StringBuilder("Conversation so far:\n");
		foreach (var message in history)
		{
			var text = message.Text;
			if (string.IsNullOrWhiteSpace(text))
				continue;

			sb.Append(message.Role == ChatRole.User ? "User: " : "Assistant: ").Append(text).Append('\n');
		}

		sb.Append("\nNew request: ").Append(userMessage);
		return sb.ToString();
	}

	private static AgentTurnResult Failure(
		string userMessage, IReadOnlyList<ChatMessage> history, string error) =>
		new()
		{
			Success = false,
			Response = string.Empty,
			UpdatedHistory = [.. history, new ChatMessage(ChatRole.User, userMessage)],
			Error = error,
			ErrorKind = AgentTurnErrorKind.Internal,
		};
}
