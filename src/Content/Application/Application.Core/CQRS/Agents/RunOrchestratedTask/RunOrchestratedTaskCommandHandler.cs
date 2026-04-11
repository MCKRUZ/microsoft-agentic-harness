using Application.AI.Common.Interfaces;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Skills;
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
	private readonly ILogger<RunOrchestratedTaskCommandHandler> _logger;

	public RunOrchestratedTaskCommandHandler(
		IAgentFactory agentFactory,
		IServiceScopeFactory scopeFactory,
		ILogger<RunOrchestratedTaskCommandHandler> logger)
	{
		_agentFactory = agentFactory;
		_scopeFactory = scopeFactory;
		_logger = logger;
	}

	public async Task<OrchestratedTaskResult> Handle(RunOrchestratedTaskCommand request, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Starting orchestrated task with {Orchestrator}, {AgentCount} available agents",
			request.OrchestratorName, request.AvailableAgents.Count);

		try
		{
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

			await ReportProgress(request, "planning", request.OrchestratorName, "Decomposing task...");

			var planMessages = new List<ChatMessage>
			{
				new(ChatRole.User, $"Decompose and execute this task: {request.TaskDescription}")
			};

			var planResponse = await orchestrator.RunAsync(planMessages, cancellationToken: cancellationToken);
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

				// Each sub-agent dispatch needs its own DI scope so that the scoped
				// AgentExecutionContext is a fresh instance — not the one already bound
				// to the orchestrator's conversation.
				ConversationResult conversationResult;
				await using (var scope = _scopeFactory.CreateAsyncScope())
				{
					var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
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

			var synthesisResponse = await orchestrator.RunAsync(synthesisMessages, cancellationToken: cancellationToken);
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
