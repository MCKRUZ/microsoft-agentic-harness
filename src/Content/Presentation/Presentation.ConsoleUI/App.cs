using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.ConsoleUI.Common.Helpers;
using Presentation.ConsoleUI.Examples;
using Spectre.Console;

namespace Presentation.ConsoleUI;

/// <summary>
/// Main application class providing an interactive Spectre.Console menu
/// for running agent examples and demonstrating harness capabilities.
/// </summary>
public class App
{
	private readonly IOptionsMonitor<AppConfig> _appConfig;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ResearchAgentExample _researchExample;
	private readonly OrchestratorExample _orchestratorExample;
	private readonly McpToolsExample _mcpToolsExample;
	private readonly ToolConverterExample _toolConverterExample;
	private readonly PersistentAgentExample _persistentAgentExample;
	private readonly A2AExample _a2aExample;
	private readonly SetupSecretsExample _setupSecretsExample;
	private readonly OptimizeExample _optimizeExample;

	public App(
		IOptionsMonitor<AppConfig> appConfig,
		ILoggerFactory loggerFactory,
		ResearchAgentExample researchExample,
		OrchestratorExample orchestratorExample,
		McpToolsExample mcpToolsExample,
		ToolConverterExample toolConverterExample,
		PersistentAgentExample persistentAgentExample,
		A2AExample a2aExample,
		SetupSecretsExample setupSecretsExample,
		OptimizeExample optimizeExample)
	{
		_appConfig = appConfig;
		_loggerFactory = loggerFactory;
		_researchExample = researchExample;
		_orchestratorExample = orchestratorExample;
		_mcpToolsExample = mcpToolsExample;
		_toolConverterExample = toolConverterExample;
		_persistentAgentExample = persistentAgentExample;
		_a2aExample = a2aExample;
		_setupSecretsExample = setupSecretsExample;
		_optimizeExample = optimizeExample;
	}

	/// <summary>
	/// Runs the interactive menu loop.
	/// </summary>
	public async Task RunAsync()
	{
		ConsoleHelper.DisplayHeader("Agentic Harness");

		while (true)
		{
			var keepRunning = await MainMenuAsync();
			if (!keepRunning) break;
		}
	}

	/// <summary>
	/// Runs a specific example non-interactively.
	/// </summary>
	public async Task RunExampleAsync(string exampleName)
	{
		switch (exampleName.ToLowerInvariant())
		{
			case "research":
				await _researchExample.RunAsync();
				break;
			case "orchestrator":
				await _orchestratorExample.RunAsync();
				break;
			case "mcp-tools":
				await _mcpToolsExample.RunAsync();
				break;
			case "tool-converter":
				await _toolConverterExample.RunAsync();
				break;
			case "persistent-agent":
				await _persistentAgentExample.RunAsync();
				break;
			case "a2a":
				await _a2aExample.RunAsync();
				break;
			case "setup-secrets":
				await _setupSecretsExample.RunAsync();
				break;
			case "optimize":
				await _optimizeExample.RunAsync();
				break;
			default:
				ConsoleHelper.DisplayError($"Unknown example: {exampleName}");
				break;
		}
	}

	private async Task<bool> MainMenuAsync()
	{
		AnsiConsole.WriteLine();

		var choice = AnsiConsole.Prompt(
			new SelectionPrompt<string>()
				.Title("[bold cornflowerblue]What would you like to do?[/]")
				.HighlightStyle(Style.Parse("cornflowerblue"))
				.AddChoiceGroup("[bold]Agents[/]",
					"Research Agent (Standalone)",
					"Orchestrator Agent (Multi-Agent)",
					"Meta-Harness Optimizer")
				.AddChoiceGroup("[bold]Advanced[/]",
					"MCP Tools Discovery",
					"Tool Converter Demo",
					"Persistent Agent (AI Foundry)",
					"A2A Agent-to-Agent")
				.AddChoiceGroup("[bold]Setup[/]",
					"Setup User Secrets",
					"Show Configuration")
				.AddChoices("Exit"));

		try
		{
			switch (choice)
			{
				case "Research Agent (Standalone)":
					await _researchExample.RunAsync();
					break;

				case "Orchestrator Agent (Multi-Agent)":
					await _orchestratorExample.RunAsync();
					break;

				case "Meta-Harness Optimizer":
					await _optimizeExample.RunAsync();
					break;

				case "MCP Tools Discovery":
					await _mcpToolsExample.RunAsync();
					break;

				case "Tool Converter Demo":
					await _toolConverterExample.RunAsync();
					break;

				case "Persistent Agent (AI Foundry)":
					await _persistentAgentExample.RunAsync();
					break;

				case "A2A Agent-to-Agent":
					await _a2aExample.RunAsync();
					break;

				case "Setup User Secrets":
					await _setupSecretsExample.RunAsync();
					break;

				case "Show Configuration":
					DisplayConfig();
					break;

				case "Exit":
					AnsiConsole.MarkupLine("[grey]Goodbye.[/]");
					return false;
			}
		}
		catch (Exception ex)
		{
			ConsoleHelper.DisplayError(ex.Message);
			_loggerFactory.CreateLogger<App>().LogError(ex, "Menu action failed");
		}

		return true;
	}

	private void DisplayConfig()
	{
		var config = _appConfig.CurrentValue;

		var table = new Table().Border(TableBorder.Rounded);
		table.AddColumn("[bold]Setting[/]");
		table.AddColumn("[bold]Value[/]");

		table.AddRow("Default Deployment", config.AI?.AgentFramework?.DefaultDeployment ?? "[grey]not set[/]");
		table.AddRow("AI Foundry Endpoint", config.AI?.AIFoundry?.ProjectEndpoint is { Length: > 0 } endpoint
			? endpoint : "[grey]not set[/]");
		table.AddRow("A2A Enabled", config.AI?.A2A?.Enabled.ToString() ?? "[grey]not set[/]");
		table.AddRow("MCP Server Name", config.AI?.MCP?.ServerName ?? "[grey]not set[/]");
		table.AddRow("MCP Servers", config.AI?.McpServers?.Servers?.Count.ToString() ?? "0");
		table.AddRow("Logs Path", config.Logging?.LogsBasePath ?? "[grey]not set[/]");
		table.AddRow("Cache Type", config.Cache?.CacheType.ToString() ?? "[grey]not set[/]");
		table.AddRow("OTel Sampling", config.Observability?.Sampling?.Enabled.ToString() ?? "[grey]not set[/]");

		AnsiConsole.Write(table);
	}
}
