using Application.Core.Agents;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using MediatR;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates the standalone ResearchAgent: single-turn and multi-turn conversations
/// using tools, MCP, and connectors.
/// </summary>
public class ResearchAgentExample
{
	private readonly ISender _sender;
	private readonly ILogger<ResearchAgentExample> _logger;

	public ResearchAgentExample(ISender sender, ILogger<ResearchAgentExample> logger)
	{
		_sender = sender;
		_logger = logger;
	}

	/// <summary>
	/// Runs the interactive research agent demo.
	/// </summary>
	public async Task RunAsync(CancellationToken cancellationToken = default)
	{
		ConsoleHelper.DisplayHeader("Research Agent", Color.CornflowerBlue);

		var agentDef = AgentDefinitions.CreateResearchAgent();
		ConsoleHelper.DisplayAgentInfo(
			agentDef.Name!,
			agentDef.Description!,
			"Standalone",
			["file_system", "github_repos (optional)"]);

		var mode = AnsiConsole.Prompt(
			new SelectionPrompt<string>()
				.Title("[bold]Select mode:[/]")
				.AddChoices("Single Turn", "Multi-Turn Conversation", "Back"));

		if (mode == "Back") return;

		if (mode == "Single Turn")
			await RunSingleTurnAsync(cancellationToken);
		else
			await RunMultiTurnAsync(cancellationToken);
	}

	private async Task RunSingleTurnAsync(CancellationToken cancellationToken)
	{
		var question = AnsiConsole.Ask<string>("[bold]Enter your research question:[/]");

		await AnsiConsole.Status()
			.Spinner(Spinner.Known.Dots)
			.SpinnerStyle(Style.Parse("cornflowerblue"))
			.StartAsync("ResearchAgent is thinking...", async _ =>
			{
				var result = await _sender.Send(new ExecuteAgentTurnCommand
				{
					AgentName = "research-agent",
					UserMessage = question,
					TurnNumber = 1
				}, cancellationToken);

				if (result.Success)
				{
					ConsoleHelper.DisplayTurnResult(1, "ResearchAgent", result.Response, result.ToolsInvoked);
				}
				else
				{
					ConsoleHelper.DisplayError($"Agent failed: {result.Error}");
				}
			});
	}

	private async Task RunMultiTurnAsync(CancellationToken cancellationToken)
	{
		AnsiConsole.MarkupLine("[grey]Enter messages (empty line to finish):[/]");
		var messages = new List<string>();

		while (true)
		{
			var input = AnsiConsole.Prompt(
				new TextPrompt<string>($"[bold]Message {messages.Count + 1}:[/]")
					.AllowEmpty());
			if (string.IsNullOrWhiteSpace(input)) break;
			messages.Add(input);
		}

		if (messages.Count == 0) return;

		AnsiConsole.MarkupLine($"\n[bold]Running {messages.Count}-turn conversation...[/]\n");

		var result = await _sender.Send(new RunConversationCommand
		{
			AgentName = "research-agent",
			UserMessages = messages,
			MaxTurns = 10,
			OnProgress = progress =>
			{
				ConsoleHelper.DisplayOrchestrationResult(
					$"Turn {progress.TurnNumber}",
					progress.AgentName,
					progress.Status,
					progress.Response?[..Math.Min(100, progress.Response.Length)]);
				return Task.CompletedTask;
			}
		}, cancellationToken);

		AnsiConsole.WriteLine();

		if (result.Success)
		{
			foreach (var turn in result.Turns)
				ConsoleHelper.DisplayTurnResult(turn.TurnNumber, "ResearchAgent", turn.AgentResponse, turn.ToolsInvoked);

			ConsoleHelper.DisplaySuccess(
				$"Conversation completed: {result.Turns.Count} turns, {result.TotalToolInvocations} tool invocations");
		}
		else
		{
			ConsoleHelper.DisplayError($"Conversation failed: {result.Error}");
		}
	}
}
