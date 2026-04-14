using Application.Core.CQRS.MetaHarness;
using MediatR;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Interactive example that runs the meta-harness optimization loop.
/// Prompts the user for an optional iteration override,
/// dispatches <see cref="RunHarnessOptimizationCommand"/> via MediatR,
/// and displays the final optimization result.
/// </summary>
public class OptimizeExample
{
	private readonly ISender _sender;
	private readonly ILogger<OptimizeExample> _logger;

	public OptimizeExample(ISender sender, ILogger<OptimizeExample> logger)
	{
		_sender = sender;
		_logger = logger;
	}

	/// <summary>
	/// Runs the interactive optimization session.
	/// </summary>
	public async Task RunAsync(CancellationToken cancellationToken = default)
	{
		ConsoleHelper.DisplayHeader("Meta-Harness Optimizer", Color.Gold1);

		var maxIterationsRaw = AnsiConsole.Prompt(
			new TextPrompt<string>("[bold]Max iterations override[/] [grey](leave empty to use config default):[/]")
				.AllowEmpty());

		int? maxIterations = null;
		if (!string.IsNullOrWhiteSpace(maxIterationsRaw))
		{
			if (int.TryParse(maxIterationsRaw, out var parsed) && parsed > 0)
				maxIterations = parsed;
			else
				AnsiConsole.MarkupLine("[yellow]Invalid input — using config default.[/]");
		}

		var runId = Guid.NewGuid();
		_logger.LogInformation("Starting optimization run {RunId}", runId);

		OptimizationResult? result = null;

		await AnsiConsole.Status()
			.Spinner(Spinner.Known.Dots)
			.SpinnerStyle(Style.Parse("gold1"))
			.StartAsync("Running optimization loop...", async _ =>
			{
				result = await _sender.Send(new RunHarnessOptimizationCommand
				{
					OptimizationRunId = runId,
					MaxIterations = maxIterations,
				}, cancellationToken);
			});

		if (result is null) return;

		if (result.IterationCount == 0)
		{
			ConsoleHelper.DisplayError(
				"No iterations completed. Check that eval tasks exist at the configured EvalTasksPath.");
			return;
		}

		AnsiConsole.MarkupLine(
			$"\n[bold green]Optimization complete.[/] Ran {result.IterationCount} iteration(s). " +
			$"Best score: [bold]{result.BestScore:P1}[/]");

		if (!string.IsNullOrEmpty(result.ProposedChangesPath))
		{
			AnsiConsole.MarkupLine($"\nBest candidate written to:");
			AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(result.ProposedChangesPath)}[/]");
			AnsiConsole.MarkupLine(
				"\n[grey]To review: inspect _proposed/ for modified skill files and system prompt.[/]");
			AnsiConsole.MarkupLine(
				"[grey]To promote: copy _proposed/ contents over the live skills/ directory.[/]");
		}
		else
		{
			AnsiConsole.MarkupLine("[grey]No proposed changes written (no successful candidates).[/]");
		}
	}
}
