using Application.Core.Agents;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
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
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<ResearchAgentExample> _logger;

	public ResearchAgentExample(ISender sender, IServiceScopeFactory scopeFactory, ILogger<ResearchAgentExample> logger)
	{
		_sender = sender;
		_scopeFactory = scopeFactory;
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

		if (!AnsiConsole.Profile.Capabilities.Interactive)
		{
			await RunHeadlessAsync(cancellationToken);
			return;
		}

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

	private async Task RunHeadlessAsync(CancellationToken cancellationToken)
	{
		AnsiConsole.MarkupLine("[grey]Non-interactive terminal detected. Running headless smoke test...[/]");

		var scenarios = new (string Name, List<string> Messages)[]
		{
			("Q&A (no tools)", [
				"What is 2 + 2? Answer in one short sentence.",
				"What is the capital of France? Answer in one short sentence.",
				"Name three primary colors. Answer in one short sentence."
			]),
			("File exploration (tool use)", [
				"Use the file_system tool to list the files in the current directory. Show me what you find.",
				"Use the file_system tool to read the file called CLAUDE.md. Summarize it in 2-3 sentences.",
				"Use the file_system tool to search for the word 'agent' in .cs files. Report the first 3 matches you find."
			]),
			("Code analysis (tool use)", [
				"Use the file_system tool to list the contents of the src/Content/Domain directory. Describe the project structure.",
				"Use the file_system tool to read the file at src/Content/Application/Application.Core/Agents/AgentDefinitions.cs and explain what agents are defined."
			]),
			("Mixed research", [
				"What is Clean Architecture? Answer in 2 sentences.",
				"Now use the file_system tool to list the files in src/Content/Application and tell me how this project follows that pattern.",
				"What are the benefits of CQRS with MediatR? Keep it to 3 bullet points."
			])
		};

		var totalSessions = 0;
		var totalTurns = 0;
		var totalTools = 0;

		foreach (var (name, messages) in scenarios)
		{
			AnsiConsole.MarkupLine($"\n[bold cornflowerblue]--- Session: {name} ({messages.Count} turns) ---[/]");

			await using var scope = _scopeFactory.CreateAsyncScope();
			var scopedSender = scope.ServiceProvider.GetRequiredService<ISender>();

			var result = await scopedSender.Send(new RunConversationCommand
			{
				AgentName = "research-agent",
				UserMessages = messages,
				MaxTurns = messages.Count,
				OnProgress = progress =>
				{
					AnsiConsole.MarkupLine($"[grey]  Turn {progress.TurnNumber} - {progress.AgentName} - {progress.Status}[/]");
					return Task.CompletedTask;
				}
			}, cancellationToken);

			AnsiConsole.WriteLine();
			if (result.Success)
			{
				foreach (var turn in result.Turns)
				{
					var preview = turn.AgentResponse.Length > 120
						? turn.AgentResponse[..120] + "..."
						: turn.AgentResponse;
					AnsiConsole.MarkupLine($"[bold]Turn {turn.TurnNumber}:[/] [grey]{Markup.Escape(turn.UserMessage[..Math.Min(80, turn.UserMessage.Length)])}[/]");
					AnsiConsole.MarkupLine($"  [white]{Markup.Escape(preview)}[/]");
					if (turn.ToolsInvoked.Count > 0)
						AnsiConsole.MarkupLine($"  [yellow]Tools: {string.Join(", ", turn.ToolsInvoked)}[/]");
				}
				totalSessions++;
				totalTurns += result.Turns.Count;
				totalTools += result.TotalToolInvocations;
			}
			else
			{
				AnsiConsole.MarkupLine($"[red]Session failed: {Markup.Escape(result.Error ?? "unknown")}[/]");
			}
		}

		AnsiConsole.WriteLine();
		ConsoleHelper.DisplaySuccess(
			$"Headless test complete: {totalSessions} sessions, {totalTurns} turns, {totalTools} tool invocations");
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
