using Application.Core.Agents;
using Application.Core.CQRS.Agents.RunOrchestratedTask;
using MediatR;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates the OrchestratorAgent: decomposes a complex task,
/// delegates to sub-agents, and synthesizes results.
/// </summary>
public class OrchestratorExample
{
	private readonly ISender _sender;
	private readonly ILogger<OrchestratorExample> _logger;

	public OrchestratorExample(ISender sender, ILogger<OrchestratorExample> logger)
	{
		_sender = sender;
		_logger = logger;
	}

	/// <summary>
	/// Runs the interactive orchestrator demo.
	/// </summary>
	public async Task RunAsync(CancellationToken cancellationToken = default)
	{
		ConsoleHelper.DisplayHeader("Orchestrator", Color.Yellow);

		var agentDef = AgentDefinitions.CreateOrchestratorAgent(["research-agent"]);
		ConsoleHelper.DisplayAgentInfo(
			agentDef.Name!,
			agentDef.Description!,
			"Multi-Agent Orchestrator",
			["Delegates to: research-agent"]);

		var taskDescription = AnsiConsole.Ask<string>("[bold]Describe the task to orchestrate:[/]");
		if (string.IsNullOrWhiteSpace(taskDescription)) return;

		AnsiConsole.MarkupLine("\n[bold]Starting orchestration...[/]\n");

		var result = await _sender.Send(new RunOrchestratedTaskCommand
		{
			OrchestratorName = "orchestrator-agent",
			TaskDescription = taskDescription,
			AvailableAgents = ["research-agent"],
			MaxTotalTurns = 20,
			OnProgress = progress =>
			{
				ConsoleHelper.DisplayOrchestrationResult(
					progress.Phase,
					progress.AgentName,
					progress.Status,
					progress.Detail);
				return Task.CompletedTask;
			}
		}, cancellationToken);

		AnsiConsole.WriteLine();

		if (result.Success)
		{
			// Show sub-agent results
			if (result.SubAgentResults.Count > 0)
			{
				AnsiConsole.MarkupLine("[bold]Sub-Agent Results:[/]");
				foreach (var sub in result.SubAgentResults)
				{
					var statusColor = sub.Success ? "green" : "red";
					AnsiConsole.MarkupLine($"  [{statusColor}]{Markup.Escape(sub.AgentName)}[/]: {Markup.Escape(sub.Subtask)}");
					if (sub.ToolsInvoked.Count > 0)
						AnsiConsole.MarkupLine($"    [grey]Tools: {string.Join(", ", sub.ToolsInvoked)}[/]");
				}
				AnsiConsole.WriteLine();
			}

			// Show final synthesis
			ConsoleHelper.DisplayInfo("Final Synthesis", Markup.Escape(result.FinalSynthesis));

			ConsoleHelper.DisplaySuccess(
				$"Orchestration completed: {result.SubAgentResults.Count} subtasks, " +
				$"{result.TotalTurns} total turns, {result.TotalToolInvocations} tool invocations");
		}
		else
		{
			ConsoleHelper.DisplayError($"Orchestration failed: {result.Error}");
		}
	}
}
