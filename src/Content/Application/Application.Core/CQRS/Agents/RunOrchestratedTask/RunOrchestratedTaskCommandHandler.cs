using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.StructuredOutput;
using Application.Common.Logging;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Skills;
using Domain.AI.Telemetry.Conventions;
using MediatR;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Agents.RunOrchestratedTask;

/// <summary>
/// Handles <see cref="RunOrchestratedTaskCommand"/> by:
/// 1. Creating the orchestrator agent
/// 2. Asking it to decompose the task into subtasks with agent assignments
/// 3. Delegating each subtask to the assigned sub-agent
/// 4. Feeding results back to the orchestrator for synthesis
/// </summary>
public class RunOrchestratedTaskCommandHandler : IRequestHandler<RunOrchestratedTaskCommand, OrchestratedTaskResult>
{
	private readonly IAgentFactory _agentFactory;
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IAgentExecutionContext _executionContext;
	private readonly IToolCallAdmissionPipeline _admissionPipeline;
	private readonly IStructuredOutputInvoker _structuredOutput;
	private readonly ILogger<RunOrchestratedTaskCommandHandler> _logger;

	// Built once: the schema attached to the planning request and the one the reply is validated against
	// are the same object, so they cannot drift apart.
	private static readonly StructuredOutputContract PlanContract = StructuredOutputSchema.Build<OrchestrationPlan>(
		"orchestration_plan", "The subtasks that decompose a task, each assigned to an available agent");

	public RunOrchestratedTaskCommandHandler(
		IAgentFactory agentFactory,
		IServiceScopeFactory scopeFactory,
		IAgentExecutionContext executionContext,
		IToolCallAdmissionPipeline admissionPipeline,
		IStructuredOutputInvoker structuredOutput,
		ILogger<RunOrchestratedTaskCommandHandler> logger)
	{
		_agentFactory = agentFactory;
		_scopeFactory = scopeFactory;
		_executionContext = executionContext;
		_admissionPipeline = admissionPipeline;
		_structuredOutput = structuredOutput;
		_logger = logger;
	}

	public async Task<OrchestratedTaskResult> Handle(RunOrchestratedTaskCommand request, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Starting orchestrated task with {Orchestrator}, {AgentCount} available agents",
			request.OrchestratorName, request.AvailableAgents.Count);

		try
		{
			// Clear any prior turn's governance decisions and loop-guard history before this task's
			// first phase. Nested MediatR sends within a conversation share one scope, so without this
			// an orchestrated task inherits the state of whatever ran before it in the same
			// conversation — and can be halted by the loop guard for calls it never made. This handler
			// previously never armed the loop guard at all, so it never needed the reset either.
			_admissionPipeline.Reset();

			// Initialized before the orchestrator is built, not after. This both governs the
			// orchestrator's own tool calls and publishes its external governance attribution (see
			// IAgentExecutionContext.Initialize), and agent construction loads skills, connects MCP
			// clients and resolves tools — all of which emit spans that would carry no attribution, and
			// be silently discarded by a governance platform, if this ran later. Only the orchestrator
			// name and conversation id are needed, and both are available here.
			//
			// Hand-rolled rather than left to AgentContextPropagationBehavior because this command is
			// not IAgentScopedRequest, so the behavior never runs for it. Without this a tenant would
			// see every sub-agent but not the orchestrator that drove them.
			//
			// The orchestrator's own conversation id is the right call-once scope here — unlike
			// DirectToolInvoker or a plan run, this handler's ConversationId is neither a fresh
			// per-call value nor shared across unrelated runs; it identifies this orchestration
			// exactly the way AgentContextPropagationBehavior's does for an ordinary agent turn.
			// Turn 1, not 0. The planning call IS this orchestrator's first turn — the handler's own
			// counter below says so ("totalTurns = 1; // Planning turn"). It passed 0 while this ran
			// after agent construction, where the number was unobservable: the prompt is composed during
			// construction and its session-state section omits the line entirely when no turn is set. Now
			// that the context is bound first, 0 would be rendered into the orchestrator's system prompt
			// as "Current turn: 0" — a counter the model reads as wrong rather than absent, since every
			// other agent starts at 1.
			_executionContext.Initialize(
				request.OrchestratorName, request.ConversationId, 1, callOnceScopeId: request.ConversationId);

			// Phase 1: Create orchestrator and get task decomposition
			var agentCatalog = BuildAgentCatalog(request.AvailableAgents);
			var orchestrator = await _agentFactory.CreateAgentFromSkillAsync(
				request.OrchestratorName,
				new SkillAgentOptions
				{
					AdditionalContext = $"""
						## Available Agents
						{agentCatalog}

						## Task
						{request.TaskDescription}

						Decompose this task into subtasks. For each subtask, specify which agent should handle it.
						Respond with the plan as JSON matching the supplied schema: a list of subtasks, each naming
						one of the available agents exactly as listed and describing what that agent should do.
						"""
				},
				cancellationToken);

			await ReportProgress(request, "planning", request.OrchestratorName, "Decomposing task...");

			var planMessages = new List<ChatMessage>
			{
				new(ChatRole.User, $"Decompose this task into subtasks for the available agents: {request.TaskDescription}")
			};

			var plan = await PlanAsync(orchestrator, planMessages, request.AvailableAgents, cancellationToken);
			if (plan.Error is not null)
			{
				return new OrchestratedTaskResult
				{
					Success = false,
					FinalSynthesis = string.Empty,
					SubAgentResults = [],
					Error = plan.Error
				};
			}

			await ReportProgress(request, "planning", request.OrchestratorName, "Plan created");

			// Phase 2: Delegate each subtask to its sub-agent
			var subtasks = plan.Subtasks;
			var subAgentResults = new List<SubAgentResult>();
			var totalTurns = 1; // Planning turn
			var totalToolInvocations = 0;

			foreach (var (agentName, subtask) in subtasks)
			{
				if (totalTurns >= request.MaxTotalTurns)
				{
					_logger.LogWarning("Max total turns reached ({MaxTurns})", request.MaxTotalTurns);
					break;
				}

				await ReportProgress(request, "delegation", agentName, $"Working on: {subtask}");

				OrchestrationMetrics.SubagentSpawns.Add(1,
					new KeyValuePair<string, object?>(AgentConventions.Name, agentName),
					new KeyValuePair<string, object?>(AgentConventions.ParentName, request.OrchestratorName));

				// Each sub-agent dispatch needs its own DI scope so that the scoped
				// AgentExecutionContext is a fresh instance — not the one already bound
				// to the orchestrator's conversation.
				ConversationResult conversationResult;
				await using (var scope = _scopeFactory.CreateAsyncScope())
				{
					var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
					// /code-review finding, investigated and NOT a bug: ConversationId = the
					// ORCHESTRATOR's own id, shared by every sub-agent this loop dispatches — which
					// also becomes their shared IToolResultStore scope (#559-563), meaning sub-agent B
					// can tool_result_fetch a result sub-agent A spilled. That is the intended
					// collaboration model for one orchestration, not an isolation gap: the same
					// scope-sharing already applies, by design, to every tool call WITHIN one ordinary
					// conversation (tool_result_fetch's whole purpose is fetching a PRIOR call's own
					// spilled result) and to a plan run's own sub-plan steps (SubPlanStepExecutor
					// inherits its parent's scope for the identical reason). The isolation boundary
					// ToolResultScopeId enforces is between DIFFERENT conversations/callers, not
					// between sub-agents collaborating on one orchestrated task under one conversation
					// id — that boundary was never claimed to exist here.
					conversationResult = await mediator.Send(new RunConversationCommand
					{
						AgentName = agentName,
						UserMessages = [subtask],
						MaxTurns = Math.Min(5, request.MaxTotalTurns - totalTurns),
						ConversationId = request.ConversationId
					}, cancellationToken);
				}

				subAgentResults.Add(new SubAgentResult
				{
					AgentName = agentName,
					Subtask = subtask,
					Result = conversationResult.FinalResponse,
					Success = conversationResult.Success,
					TurnsUsed = conversationResult.Turns.Count,
					ToolsInvoked = conversationResult.Turns.SelectMany(t => t.ToolsInvoked).Distinct().ToList()
				});

				totalTurns += conversationResult.Turns.Count;
				totalToolInvocations += conversationResult.TotalToolInvocations;

				await ReportProgress(request, "delegation", agentName,
					conversationResult.Success ? "Completed" : $"Failed: {conversationResult.Error}");
			}

			// Phase 3: Synthesize results
			await ReportProgress(request, "synthesis", request.OrchestratorName, "Synthesizing results...");

			var synthesisPrompt = BuildSynthesisPrompt(request.TaskDescription, subAgentResults);
			var synthesisMessages = new List<ChatMessage>(planMessages)
			{
				new(ChatRole.Assistant, plan.RawPlan),
				new(ChatRole.User, synthesisPrompt)
			};

			var synthesisResponse = await RunOrchestratorGovernedAsync(
				() => orchestrator.RunAsync(synthesisMessages, cancellationToken: cancellationToken));
			var finalSynthesis = ExtractContent(synthesisResponse);
			totalTurns++;

			_logger.LogInformation(
				"Orchestration completed: {SubtaskCount} subtasks, {TotalTurns} total turns, {ToolCount} tool invocations",
				subAgentResults.Count, totalTurns, totalToolInvocations);

			return new OrchestratedTaskResult
			{
				Success = true,
				FinalSynthesis = finalSynthesis,
				SubAgentResults = subAgentResults,
				TotalTurns = totalTurns,
				TotalToolInvocations = totalToolInvocations
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Orchestrated task failed for {Orchestrator}", request.OrchestratorName);

			return new OrchestratedTaskResult
			{
				Success = false,
				FinalSynthesis = string.Empty,
				SubAgentResults = [],
				Error = ex.Message
			};
		}
	}

	private async Task<T> RunOrchestratorGovernedAsync<T>(Func<Task<T>> orchestratorCall)
	{
		// Expose this scope's admission chain to the governed tool wrappers for the orchestrator's own
		// calls, scoped tightly around each so interleaved sub-agent turns (which arm their own chain in
		// a child scope) are unaffected.
		//
		// This used to arm the governor, the classification gate and the observer chain individually —
		// and never armed the loop guard, so the orchestrator was the one agent that could spin on a
		// repeated tool call unchecked. There is now one value to publish and no subset to get wrong.
		//
		// Deliberately NOT reset here: this method runs once for planning and once for synthesis, and
		// they are two phases of one unit of work. The loop guard should see the orchestrator's calls
		// across both — repeating in synthesis what it already did while planning is exactly the spin
		// worth catching — and the governance trace should accumulate across both rather than losing
		// the planning phase. The single reset lives at the top of Handle.
		using (ToolAdmissionAccessor.Begin(_admissionPipeline))
		{
			return await orchestratorCall();
		}
	}

	private static string BuildAgentCatalog(IReadOnlyList<string> agentNames)
	{
		return string.Join("\n", agentNames.Select(name => $"- **{name}**: Available for subtask delegation"));
	}

	/// <summary>
	/// Asks the orchestrator for its decomposition as a typed <see cref="OrchestrationPlan"/> and checks it
	/// against the agents actually available. Any failure returns a stable <see cref="OrchestrationErrors"/>
	/// code; what the model returned is logged, never surfaced.
	/// </summary>
	private async Task<ResolvedPlan> PlanAsync(
		AIAgent orchestrator, IReadOnlyList<ChatMessage> planMessages, IReadOnlyList<string> availableAgents,
		CancellationToken cancellationToken)
	{
		// The orchestrator itself is the chat client, so its skill instructions, governance middleware and
		// content safety stay in force, which a bare provider client would bypass. Non-ChatClientAgent
		// orchestrators are allowed because only the response format is needed, and that is the one option
		// the adapter always forwards. The orchestrator is tool-capable and the invoker's repair attempt is
		// a fresh, stateless run, so a planning call that used tools can use them twice; the loop guard and
		// call-once policy see both runs because the pipeline is published for the whole call.
		var chatClient = orchestrator.AsIChatClient(allowNonChatClientAgents: true);

		var parsed = await RunOrchestratorGovernedAsync(() => _structuredOutput.InvokeAsync<OrchestrationPlan>(
			chatClient, PlanContract, planMessages, chatOptions: null, cancellationToken));

		if (parsed.Outcome == StructuredOutcome.InvocationFailed)
		{
			// The call itself failed (provider, content safety, an unsupported schema request): not a plan
			// the model got wrong, so it is not reported as one.
			_logger.LogWarning("Orchestration planning call failed: {Reason}", parsed.ErrorMessage);
			return ResolvedPlan.Fail(OrchestrationErrors.PlanUnavailable);
		}

		// "required" demands the property be present, not non-null, so a null list or element still parses.
		if (!parsed.IsSuccess || parsed.Value?.Subtasks is not { } plannedSubtasks)
		{
			_logger.LogWarning(
				"Orchestration plan could not be read ({Outcome}): {Reason}", parsed.Outcome, parsed.ErrorMessage);
			return ResolvedPlan.Fail(OrchestrationErrors.PlanInvalid);
		}

		if (plannedSubtasks.Count == 0)
		{
			_logger.LogWarning("Orchestration plan contained no subtasks");
			return ResolvedPlan.Fail(OrchestrationErrors.PlanEmpty);
		}

		var subtasks = new List<(string AgentName, string Subtask)>(plannedSubtasks.Count);
		foreach (var planned in plannedSubtasks)
		{
			if (planned is null || string.IsNullOrWhiteSpace(planned.Description))
			{
				_logger.LogWarning("Orchestration plan has a missing subtask or one with a blank description");
				return ResolvedPlan.Fail(OrchestrationErrors.PlanInvalid);
			}

			// A plan naming an agent that does not exist is rejected whole: running the rest would
			// synthesize an answer that silently omits part of the work.
			var matchedAgent = availableAgents.FirstOrDefault(a =>
				a.Equals(planned.Agent?.Trim(), StringComparison.OrdinalIgnoreCase));
			if (matchedAgent is null)
			{
				// The name is the model's, so it is bounded and stripped of control characters before it
				// reaches a log line.
				_logger.LogWarning(
					"Orchestration plan assigns a subtask to an agent that is not available: {Agent}",
					LoggingHelper.SanitizeForLog(planned.Agent));
				return ResolvedPlan.Fail(OrchestrationErrors.PlanUnknownAgent);
			}

			subtasks.Add((matchedAgent, planned.Description.Trim()));
		}

		return ResolvedPlan.Ok(subtasks, parsed.RawOutput ?? string.Empty);
	}

	/// <summary>A validated plan, or the stable code explaining why there isn't one.</summary>
	private readonly record struct ResolvedPlan(
		IReadOnlyList<(string AgentName, string Subtask)> Subtasks, string RawPlan, string? Error)
	{
		public static ResolvedPlan Ok(IReadOnlyList<(string AgentName, string Subtask)> subtasks, string rawPlan)
			=> new(subtasks, rawPlan, null);

		public static ResolvedPlan Fail(string error) => new([], string.Empty, error);
	}

	private static string BuildSynthesisPrompt(string originalTask, List<SubAgentResult> results)
	{
		var resultsSummary = string.Join("\n\n", results.Select(r =>
			$"### {r.AgentName} — {r.Subtask}\n" +
			$"**Status:** {(r.Success ? "Success" : "Failed")}\n" +
			$"**Result:**\n{r.Result}"));

		return $"""
			The subtask results are in. Synthesize them into a cohesive response for the original task.

			## Original Task
			{originalTask}

			## Subtask Results
			{resultsSummary}

			Provide a comprehensive synthesis that combines all results into a clear, actionable response.
			""";
	}

	private static string ExtractContent(object? response)
	{
		if (response is null) return string.Empty;
		if (response is string str) return str;

		if (response is ChatResponse chatResponse)
		{
			return string.Join("\n", chatResponse.Messages
				.Where(m => m.Role == ChatRole.Assistant)
				.SelectMany(m => m.Contents.OfType<TextContent>())
				.Select(tc => tc.Text));
		}

		var contentProp = response.GetType().GetProperty("Content");
		return contentProp?.GetValue(response)?.ToString() ?? response.ToString() ?? string.Empty;
	}

	private static async Task ReportProgress(RunOrchestratedTaskCommand request, string phase, string agent, string status)
	{
		if (request.OnProgress != null)
		{
			await request.OnProgress(new OrchestrationProgress
			{
				Phase = phase,
				AgentName = agent,
				Status = status
			});
		}
	}
}
