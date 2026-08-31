using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services.Governance;
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
	private readonly ILogger<RunOrchestratedTaskCommandHandler> _logger;

	public RunOrchestratedTaskCommandHandler(
		IAgentFactory agentFactory,
		IServiceScopeFactory scopeFactory,
		IAgentExecutionContext executionContext,
		IToolCallAdmissionPipeline admissionPipeline,
		ILogger<RunOrchestratedTaskCommandHandler> logger)
	{
		_agentFactory = agentFactory;
		_scopeFactory = scopeFactory;
		_executionContext = executionContext;
		_admissionPipeline = admissionPipeline;
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
						Respond with a plan in this format:
						SUBTASK: [agent_name] - [subtask description]
						"""
				},
				cancellationToken);

			// Govern the orchestrator's OWN tool calls (planning + synthesis). This command is not
			// IAgentScopedRequest, so the pipeline doesn't initialize the execution context — set the
			// orchestrator identity here so the governor can resolve it. Sub-agent delegation runs in
			// its own child scope and governs itself per turn.
			//
			// The orchestrator's own conversation id is the right call-once scope here — unlike
			// DirectToolInvoker or a plan run, this handler's ConversationId is neither a fresh
			// per-call value nor shared across unrelated runs; it identifies this orchestration
			// exactly the way AgentContextPropagationBehavior's does for an ordinary agent turn.
			_executionContext.Initialize(
				request.OrchestratorName, request.ConversationId, 0, callOnceScopeId: request.ConversationId);

			await ReportProgress(request, "planning", request.OrchestratorName, "Decomposing task...");

			var planMessages = new List<ChatMessage>
			{
				new(ChatRole.User, $"Decompose and execute this task: {request.TaskDescription}")
			};

			var planResponse = await RunOrchestratorGovernedAsync(orchestrator, planMessages, cancellationToken);
			var planText = ExtractContent(planResponse);

			await ReportProgress(request, "planning", request.OrchestratorName, "Plan created");

			// Phase 2: Parse subtasks and delegate to sub-agents
			var subtasks = ParseSubtasks(planText, request.AvailableAgents);
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
				new(ChatRole.Assistant, planText),
				new(ChatRole.User, synthesisPrompt)
			};

			var synthesisResponse = await RunOrchestratorGovernedAsync(orchestrator, synthesisMessages, cancellationToken);
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

	private async Task<object?> RunOrchestratorGovernedAsync(
		AIAgent orchestrator, List<ChatMessage> messages, CancellationToken cancellationToken)
	{
		// Expose this scope's admission chain to the governed tool wrappers for the orchestrator's own
		// RunAsync, scoped tightly around the call so interleaved sub-agent turns (which arm their own
		// chain in a child scope) are unaffected.
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
			return await orchestrator.RunAsync(messages, cancellationToken: cancellationToken);
		}
	}

	private static string BuildAgentCatalog(IReadOnlyList<string> agentNames)
	{
		return string.Join("\n", agentNames.Select(name => $"- **{name}**: Available for subtask delegation"));
	}

	private static List<(string AgentName, string Subtask)> ParseSubtasks(
		string planText, IReadOnlyList<string> availableAgents)
	{
		var subtasks = new List<(string, string)>();
		var lines = planText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		foreach (var line in lines)
		{
			// Match "SUBTASK: agent_name - description" or similar patterns
			var trimmed = line.Trim();
			if (!trimmed.StartsWith("SUBTASK:", StringComparison.OrdinalIgnoreCase))
				continue;

			var content = trimmed["SUBTASK:".Length..].Trim();
			var dashIndex = content.IndexOf(" - ", StringComparison.Ordinal);
			if (dashIndex <= 0)
				continue;

			var agentName = content[..dashIndex].Trim();
			var subtask = content[(dashIndex + 3)..].Trim();

			// Validate agent is available
			var matchedAgent = availableAgents.FirstOrDefault(a =>
				a.Equals(agentName, StringComparison.OrdinalIgnoreCase));

			if (matchedAgent != null && !string.IsNullOrEmpty(subtask))
				subtasks.Add((matchedAgent, subtask));
		}

		// Fallback: if no SUBTASK lines found, assign entire task to first available agent
		if (subtasks.Count == 0 && availableAgents.Count > 0)
			subtasks.Add((availableAgents[0], planText));

		return subtasks;
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
