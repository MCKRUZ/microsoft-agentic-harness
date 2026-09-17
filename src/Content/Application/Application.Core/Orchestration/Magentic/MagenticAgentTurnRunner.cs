using System.Text;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Governance;
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
		string userMessage,
		IReadOnlyList<ChatMessage> conversationHistory,
		MagenticTurnOverrides overrides,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(overrides);
		ArgumentNullException.ThrowIfNull(supervisor);
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

		// Overrides apply to the manager only — the agent the caller actually addressed — the same way
		// a single-agent turn's SystemPromptOverride/DeploymentOverride/Temperature apply only to the
		// one agent named on the request, never to whatever it delegates to internally.
		var manager = await BuildAgentAsync(supervisor, cancellationToken, overrides);
		var participants = new List<AIAgent>(supervisor.Participants.Count);
		foreach (var participantId in supervisor.Participants)
		{
			var participantDef = _agentRegistry.TryGet(participantId);
			if (participantDef is null)
			{
				_logger.LogWarning(
					"Supervisor {AgentId} names participant {ParticipantId}, which is not a registered agent; skipping it",
					supervisor.Id, participantId);
				continue;
			}

			participants.Add(await BuildAgentAsync(participantDef, cancellationToken));
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
		var responseText = workflow.FinalOutput ?? string.Empty;
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
	/// Builds an <see cref="AIAgent"/> for one agent definition — the manager or a participant —
	/// the same way <c>ExecuteAgentTurnCommandHandler</c> resolves any agent: through its own
	/// declared skills, falling back to its id as a bare skill id when it declares none.
	/// </summary>
	/// <param name="overrides">
	/// Per-turn overrides to apply — pass a non-null instance only for the manager (see the call site
	/// in <see cref="RunTurnAsync"/>); a participant never receives caller-supplied overrides.
	/// </param>
	private Task<AIAgent> BuildAgentAsync(
		AgentDefinition agentDef, CancellationToken cancellationToken, MagenticTurnOverrides? overrides = null)
	{
		IReadOnlyList<string> skillIds = agentDef.Skills is { Count: > 0 } ? agentDef.Skills : [agentDef.Id];

		return _agentFactory.CreateAgentFromSkillsAsync(
			skillIds,
			new SkillAgentOptions
			{
				AgentInstructions = agentDef.Instructions,
				AllowedTools = agentDef.AllowedTools,
				OwningAgentId = agentDef.Id,
				AdditionalContext = overrides?.SystemPromptOverride,
				DeploymentName = overrides?.DeploymentOverride,
				Temperature = overrides?.Temperature,
			},
			cancellationToken);
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
