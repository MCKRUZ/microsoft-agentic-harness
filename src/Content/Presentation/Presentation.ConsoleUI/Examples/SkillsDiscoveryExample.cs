using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Interactive example demonstrating progressive disclosure (3-tier loading) of skills
/// and context budget tracking for an agent.
/// </summary>
/// <remarks>
/// <para>
/// This example shows:
/// - Tier 1 (Index Card): Metadata only (~100 tokens) — skill name, description, estimated size.
/// - Tier 2 (Folder): Full instructions (~5000 tokens) — content preview and loading.
/// - Tier 3 (Filing Cabinet): Templates, scripts, references — never loaded into context.
/// - Context budget tracking: allocations across system prompt, skills, tools, and history.
/// - Budget assessment: detecting when remaining budget is insufficient.
/// </para>
/// </remarks>
public class SkillsDiscoveryExample
{
	private readonly IContextBudgetTracker _budgetTracker;
	private readonly ILogger<SkillsDiscoveryExample> _logger;

	public SkillsDiscoveryExample(
		IContextBudgetTracker budgetTracker,
		ILogger<SkillsDiscoveryExample> logger)
	{
		_budgetTracker = budgetTracker;
		_logger = logger;
	}

	/// <summary>
	/// Runs the interactive skills discovery and budget tracking session.
	/// </summary>
	public async Task RunAsync(CancellationToken cancellationToken = default)
	{
		ConsoleHelper.DisplayHeader("Skills Discovery & Context Budget", Color.Cyan1);

		// Step 1: Discover skills
		ConsoleHelper.DisplayStep(1, 4, "Discover available skills");
		var skills = DiscoverSkills();
		DisplaySkillsList(skills);

		// Step 2: Tier 1 view (metadata)
		ConsoleHelper.DisplayStep(2, 4, "Tier 1 - Index Card view (metadata only)");
		DisplayTier1View(skills);

		// Step 3: Tier 2 view (instructions)
		ConsoleHelper.DisplayStep(3, 4, "Tier 2 - Folder view (full instructions)");
		DisplayTier2View(skills);

		// Step 4: Budget tracking
		ConsoleHelper.DisplayStep(4, 4, "Context budget tracking and assessment");
		await DisplayBudgetTrackingAsync(cancellationToken);

		ConsoleHelper.DisplaySuccess("Skills discovery and budget tracking demonstration complete.");
	}

	/// <summary>
	/// Discovers skills. In a live environment, this would scan SKILL.md files from disk.
	/// For this example, it uses synthetic skill data as fallback.
	/// </summary>
	private List<SkillSummary> DiscoverSkills()
	{
		// Attempt to discover real SKILL.md files from the skills directory
		var skills = TryDiscoverRealSkills();

		// Fall back to synthetic data if no real skills found
		if (skills.Count == 0)
		{
			_logger.LogInformation("No SKILL.md files found; using synthetic skill data");
			skills = GetSyntheticSkills();
		}

		return skills;
	}

	/// <summary>
	/// Attempts to discover real SKILL.md files from the skills directory.
	/// </summary>
	private List<SkillSummary> TryDiscoverRealSkills()
	{
		var skills = new List<SkillSummary>();

		// Look for SKILL.md files in the application skills directory
		// In a built output, this directory may not exist; gracefully fall back to synthetic data
		var baseSkillsPath = Path.Combine(
			AppContext.BaseDirectory,
			"..", "..", "..", "Application", "Application.Core", "Agents", "Skills");

		if (!Directory.Exists(baseSkillsPath))
		{
			_logger.LogInformation("Skills directory not found at {Path}", baseSkillsPath);
			return skills;
		}

		try
		{
			var skillDirs = Directory.EnumerateDirectories(baseSkillsPath).ToList();
			foreach (var dir in skillDirs)
			{
				var skillMdPath = Path.Combine(dir, "SKILL.md");
				if (File.Exists(skillMdPath))
				{
					var content = File.ReadAllText(skillMdPath);
					var dirName = new DirectoryInfo(dir).Name;
					var estimatedTokens = EstimateTokens(content);
					skills.Add(new SkillSummary(dirName, content, estimatedTokens));
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Error discovering real skills; falling back to synthetic data");
		}

		return skills;
	}

	/// <summary>
	/// Returns synthetic skill data for demonstration when real skills are not available.
	/// </summary>
	private List<SkillSummary> GetSyntheticSkills()
	{
		return new List<SkillSummary>
		{
			new("research-agent",
				@"# Research Agent

## Instructions
This skill enables advanced research capabilities with RAG integration and web search.
It orchestrates multi-step research workflows, decomposes complex queries, and synthesizes findings.

## Capabilities
- Query decomposition for complex research tasks
- Hybrid retrieval combining semantic + keyword search
- Web search for live data augmentation
- Citation tracking for result provenance
- Relevance feedback for iterative refinement",
				4200),

			new("orchestrator-agent",
				@"# Orchestrator Agent

## Instructions
This skill handles task decomposition, multi-agent orchestration, and workflow coordination.
It breaks down complex problems into subtasks, dispatches them to specialized agents, and synthesizes results.

## Workflow Stages
1. Task Analysis: understand requirements, identify constraints
2. Decomposition: break into independent subtasks
3. Dispatch: route to appropriate specialist agents
4. Aggregation: collect and synthesize results
5. Refinement: evaluate quality and iterate if needed",
				5800),

			new("echo-test",
				@"# Echo Test Skill

A minimal test skill for demonstration purposes.
It echoes back inputs with metadata.",
				350),
		};
	}

	/// <summary>
	/// Displays the list of discovered skills in a table.
	/// </summary>
	private void DisplaySkillsList(List<SkillSummary> skills)
	{
		var table = new Table().Border(TableBorder.Rounded);
		table.AddColumn("[bold]Skill Name[/]");
		table.AddColumn("[bold]Estimated Tokens[/]");
		table.AddColumn("[bold]Tier 1 (~100)[/]");
		table.AddColumn("[bold]Tier 2 (~5000)[/]");

		foreach (var skill in skills)
		{
			var tier1Tokens = Math.Min(skill.EstimatedTokens / 50, 100); // Rough estimate: metadata is small
			var tier2Tokens = Math.Max(skill.EstimatedTokens - tier1Tokens, 0);

			table.AddRow(
				$"[cornflowerblue]{Markup.Escape(skill.Name)}[/]",
				$"[yellow]{skill.EstimatedTokens}[/]",
				$"[green]~{tier1Tokens}[/]",
				$"[cyan]~{tier2Tokens}[/]");
		}

		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();
	}

	/// <summary>
	/// Displays Tier 1 view: metadata only, showing what the agent knows without loading full content.
	/// </summary>
	private void DisplayTier1View(List<SkillSummary> skills)
	{
		AnsiConsole.MarkupLine("[grey]Tier 1 view loaded in agent context (metadata only, ~100 tokens per skill):[/]");
		AnsiConsole.WriteLine();

		var tier1Table = new Table().Border(TableBorder.Square);
		tier1Table.AddColumn("[bold]Skill[/]");
		tier1Table.AddColumn("[bold]Category[/]");
		tier1Table.AddColumn("[bold]Est. Size[/]");

		foreach (var skill in skills)
		{
			var category = skill.Name switch
			{
				"research-agent" => "RAG",
				"orchestrator-agent" => "Orchestration",
				_ => "Utility"
			};

			var sizeLabel = skill.EstimatedTokens switch
			{
				< 1000 => "[green]Small[/]",
				< 5000 => "[yellow]Medium[/]",
				_ => "[red]Large[/]"
			};

			tier1Table.AddRow(
				$"[cornflowerblue]{Markup.Escape(skill.Name)}[/]",
				category,
				sizeLabel);
		}

		AnsiConsole.Write(tier1Table);
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine("[grey]Agent now knows: \"I have these skills available.\" Context cost: ~{0} tokens.[/]",
			skills.Count * 100);
		AnsiConsole.WriteLine();
	}

	/// <summary>
	/// Displays Tier 2 view: loads and previews full instructions for the first 2 skills.
	/// </summary>
	private void DisplayTier2View(List<SkillSummary> skills)
	{
		AnsiConsole.MarkupLine("[grey]Tier 2 view: loading full instructions on demand[/]");
		AnsiConsole.WriteLine();

		var skillsToPreview = skills.Take(2).ToList();

		foreach (var skill in skillsToPreview)
		{
			var panel = new Panel(new Markup(TruncateText(skill.Content, 300)))
			{
				Header = new PanelHeader($"[cornflowerblue]{Markup.Escape(skill.Name)} (Tier 2)[/]"),
				Border = BoxBorder.Rounded
			};
			AnsiConsole.Write(panel);

			AnsiConsole.MarkupLine($"[grey]Tier 2 loaded: {skill.EstimatedTokens} estimated tokens.[/]");
			AnsiConsole.WriteLine();
		}

		AnsiConsole.MarkupLine(
			"[grey](Tier 3 resources like templates and scripts remain on disk — never loaded into context.)[/]");
		AnsiConsole.WriteLine();
	}

	/// <summary>
	/// Demonstrates context budget tracking with allocation recording and assessment.
	/// </summary>
	private async Task DisplayBudgetTrackingAsync(CancellationToken cancellationToken = default)
	{
		const string agentName = "demo-agent";
		const int totalBudget = 128_000; // 128K token context window

		// Reset tracker for this agent
		_budgetTracker.Reset(agentName);

		// Record allocations across components
		AnsiConsole.MarkupLine("[grey]Recording allocations for [bold]{0}[/]:[/]", agentName);

		_budgetTracker.RecordAllocation(agentName, "system_prompt", 2500);
		_logger.LogInformation("Recorded system_prompt: 2500 tokens");

		_budgetTracker.RecordAllocation(agentName, "skills_tier1", 800);
		_logger.LogInformation("Recorded skills_tier1: 800 tokens");

		_budgetTracker.RecordAllocation(agentName, "skills_tier2", 5200);
		_logger.LogInformation("Recorded skills_tier2: 5200 tokens");

		_budgetTracker.RecordAllocation(agentName, "tool_schemas", 3100);
		_logger.LogInformation("Recorded tool_schemas: 3100 tokens");

		_budgetTracker.RecordAllocation(agentName, "conversation_history", 45000);
		_logger.LogInformation("Recorded conversation_history: 45000 tokens");

		AnsiConsole.WriteLine();

		// Display breakdown
		DisplayBudgetBreakdown(agentName, totalBudget);

		// Simulate large allocation to trigger budget exceeded
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine("[grey]Attempting large allocation (100K tokens) to trigger budget exceeded...[/]");

		try
		{
			_budgetTracker.EnsureBudget(agentName, 100_000, totalBudget);
			AnsiConsole.MarkupLine("[red]Unexpected: large allocation did not throw.[/]");
		}
		catch (Exception ex)
		{
			ConsoleHelper.DisplayInfo("Budget Exceeded", $"[yellow]{Markup.Escape(ex.Message)}[/]");
		}

		AnsiConsole.WriteLine();

		// Display final assessment
		var assessment = _budgetTracker.AssessContinuation(agentName, totalBudget);
		DisplayBudgetAssessment(assessment);

		await Task.CompletedTask;
	}

	/// <summary>
	/// Displays the per-component budget breakdown in a table.
	/// </summary>
	private void DisplayBudgetBreakdown(string agentName, int totalBudget)
	{
		var breakdown = _budgetTracker.GetBreakdown(agentName);
		var totalAllocated = _budgetTracker.GetTotalAllocated(agentName);
		var remaining = _budgetTracker.GetRemainingBudget(agentName, totalBudget);

		var table = new Table().Border(TableBorder.Rounded);
		table.AddColumn("[bold]Component[/]");
		table.AddColumn("[bold]Tokens[/]");
		table.AddColumn("[bold]% of Budget[/]");

		foreach (var (component, tokens) in breakdown.OrderByDescending(x => x.Value))
		{
			var percentage = (double)tokens / totalBudget * 100;
			var percentageColor = percentage > 50 ? "red" : percentage > 30 ? "yellow" : "green";
			table.AddRow(
				Markup.Escape(component),
				$"[yellow]{tokens}[/]",
				$"[{percentageColor}]{percentage:F1}%[/]");
		}

		table.AddRow(
			"[bold]TOTAL ALLOCATED[/]",
			$"[bold yellow]{totalAllocated}[/]",
			$"[bold]{(double)totalAllocated / totalBudget * 100:F1}%[/]");

		table.AddRow(
			"[bold]REMAINING[/]",
			$"[bold green]{remaining}[/]",
			$"[bold green]{(double)remaining / totalBudget * 100:F1}%[/]");

		AnsiConsole.Write(table);
		AnsiConsole.WriteLine();
	}

	/// <summary>
	/// Displays the budget assessment with recommendation and tracking metrics.
	/// </summary>
	private void DisplayBudgetAssessment(BudgetAssessment assessment)
	{
		var actionColor = assessment.Action switch
		{
			TokenBudgetAction.Continue => "green",
			TokenBudgetAction.Stop => "red",
			TokenBudgetAction.Nudge => "yellow",
			_ => "default"
		};

		var panel = new Panel(
			new Markup(
				$"[{actionColor}]Action:[/] {assessment.Action}\n" +
				$"[grey]Reason:[/] {Markup.Escape(assessment.Reason)}\n" +
				$"[grey]Continuation Turns:[/] {assessment.ContinuationCount}\n" +
				$"[grey]Budget Usage:[/] {assessment.CompletionPercentage:P1}"))
		{
			Header = new PanelHeader("[bold cyan]Budget Assessment[/]"),
			Border = BoxBorder.Rounded
		};
		AnsiConsole.Write(panel);
	}

	/// <summary>
	/// Truncates text to a maximum length with ellipsis.
	/// </summary>
	private string TruncateText(string text, int maxLength)
	{
		if (text.Length <= maxLength) return text;
		return text[..maxLength] + "...";
	}

	/// <summary>
	/// Estimates tokens from text length using the common approximation: length / 4.
	/// </summary>
	private int EstimateTokens(string text) =>
		string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

	/// <summary>
	/// Represents a discovered skill with its content and token estimate.
	/// </summary>
	private record SkillSummary(string Name, string Content, int EstimatedTokens);
}
