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
	private readonly RagPipelineExample _ragPipelineExample;
	private readonly KnowledgeGraphMemoryExample _knowledgeGraphMemoryExample;
	private readonly KnowledgeGraphComplianceExample _knowledgeGraphComplianceExample;
	private readonly GovernanceSanitizationExample _governanceSanitizationExample;
	private readonly EscalationApprovalsExample _escalationApprovalsExample;
	private readonly SkillsDiscoveryExample _skillsDiscoveryExample;
	private readonly DriftDetectionExample _driftDetectionExample;
	private readonly LearningsLogExample _learningsLogExample;
	private readonly ObservabilityBudgetExample _observabilityBudgetExample;
	private readonly MultiSourceRetrievalExample _multiSourceRetrievalExample;
	private readonly SandboxCapabilitiesExample _sandboxCapabilitiesExample;
	private readonly PipelineBehaviorsExample _pipelineBehaviorsExample;
	private readonly SkillTrainingExample _skillTrainingExample;
	private readonly MagenticOrchestrationExample _magenticOrchestrationExample;
	private readonly A2ASreToWorkspaceExample _a2aSreToWorkspaceExample;

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
		OptimizeExample optimizeExample,
		RagPipelineExample ragPipelineExample,
		KnowledgeGraphMemoryExample knowledgeGraphMemoryExample,
		KnowledgeGraphComplianceExample knowledgeGraphComplianceExample,
		GovernanceSanitizationExample governanceSanitizationExample,
		EscalationApprovalsExample escalationApprovalsExample,
		SkillsDiscoveryExample skillsDiscoveryExample,
		DriftDetectionExample driftDetectionExample,
		LearningsLogExample learningsLogExample,
		ObservabilityBudgetExample observabilityBudgetExample,
		MultiSourceRetrievalExample multiSourceRetrievalExample,
		SandboxCapabilitiesExample sandboxCapabilitiesExample,
		PipelineBehaviorsExample pipelineBehaviorsExample,
		SkillTrainingExample skillTrainingExample,
		MagenticOrchestrationExample magenticOrchestrationExample,
		A2ASreToWorkspaceExample a2aSreToWorkspaceExample)
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
		_ragPipelineExample = ragPipelineExample;
		_knowledgeGraphMemoryExample = knowledgeGraphMemoryExample;
		_knowledgeGraphComplianceExample = knowledgeGraphComplianceExample;
		_governanceSanitizationExample = governanceSanitizationExample;
		_escalationApprovalsExample = escalationApprovalsExample;
		_skillsDiscoveryExample = skillsDiscoveryExample;
		_driftDetectionExample = driftDetectionExample;
		_learningsLogExample = learningsLogExample;
		_observabilityBudgetExample = observabilityBudgetExample;
		_multiSourceRetrievalExample = multiSourceRetrievalExample;
		_sandboxCapabilitiesExample = sandboxCapabilitiesExample;
		_pipelineBehaviorsExample = pipelineBehaviorsExample;
		_skillTrainingExample = skillTrainingExample;
		_magenticOrchestrationExample = magenticOrchestrationExample;
		_a2aSreToWorkspaceExample = a2aSreToWorkspaceExample;
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
			case "rag-pipeline":
				await _ragPipelineExample.RunAsync();
				break;
			case "knowledge-graph-memory":
				await _knowledgeGraphMemoryExample.RunAsync();
				break;
			case "knowledge-graph-compliance":
				await _knowledgeGraphComplianceExample.RunAsync();
				break;
			case "governance-sanitization":
				await _governanceSanitizationExample.RunAsync();
				break;
			case "escalation-approvals":
				await _escalationApprovalsExample.RunAsync();
				break;
			case "skills-discovery":
				await _skillsDiscoveryExample.RunAsync();
				break;
			case "drift-detection":
				await _driftDetectionExample.RunAsync();
				break;
			case "learnings-log":
				await _learningsLogExample.RunAsync();
				break;
			case "observability-budget":
				await _observabilityBudgetExample.RunAsync();
				break;
			case "multi-source-retrieval":
				await _multiSourceRetrievalExample.RunAsync();
				break;
			case "sandbox-capabilities":
				await _sandboxCapabilitiesExample.RunAsync();
				break;
			case "pipeline-behaviors":
				await _pipelineBehaviorsExample.RunAsync();
				break;
			case "skill-training":
				await _skillTrainingExample.RunAsync();
				break;
			case "magentic":
				await _magenticOrchestrationExample.RunAsync();
				break;
			case "a2a-sre-workspace":
				await _a2aSreToWorkspaceExample.RunAsync();
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
					"Magentic Orchestration (Multi-Agent)",
					"Persistent Agent (AI Foundry)",
					"A2A Agent-to-Agent",
					"A2A SRE-to-Workspace")
				.AddChoiceGroup("[bold]RAG & Retrieval[/]",
					"RAG Pipeline Demo",
					"Multi-Source Retrieval")
				.AddChoiceGroup("[bold]Knowledge Graph[/]",
					"Knowledge Graph Memory",
					"Knowledge Graph Compliance")
				.AddChoiceGroup("[bold]Governance & Safety[/]",
					"Response Sanitization",
					"Escalation & Approvals",
					"Pipeline Behaviors")
				.AddChoiceGroup("[bold]Skills & Tools[/]",
					"Skills Discovery & Budget",
					"Tool Converter Demo",
					"MCP Tools Discovery",
					"Sandbox Capabilities")
				.AddChoiceGroup("[bold]Observability[/]",
					"Drift Detection",
					"Learnings Log",
					"Budget & Health Tracking")
				.AddChoiceGroup("[bold]Optimization[/]",
					"Meta-Harness Optimizer",
					"Skill Training (SkillOpt)")
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

				case "Magentic Orchestration (Multi-Agent)":
					await _magenticOrchestrationExample.RunAsync();
					break;

				case "Persistent Agent (AI Foundry)":
					await _persistentAgentExample.RunAsync();
					break;

				case "A2A Agent-to-Agent":
					await _a2aExample.RunAsync();
					break;

				case "A2A SRE-to-Workspace":
					await _a2aSreToWorkspaceExample.RunAsync();
					break;

				case "RAG Pipeline Demo":
					await _ragPipelineExample.RunAsync();
					break;

				case "Multi-Source Retrieval":
					await _multiSourceRetrievalExample.RunAsync();
					break;

				case "Knowledge Graph Memory":
					await _knowledgeGraphMemoryExample.RunAsync();
					break;

				case "Knowledge Graph Compliance":
					await _knowledgeGraphComplianceExample.RunAsync();
					break;

				case "Response Sanitization":
					await _governanceSanitizationExample.RunAsync();
					break;

				case "Escalation & Approvals":
					await _escalationApprovalsExample.RunAsync();
					break;

				case "Pipeline Behaviors":
					await _pipelineBehaviorsExample.RunAsync();
					break;

				case "Skills Discovery & Budget":
					await _skillsDiscoveryExample.RunAsync();
					break;

				case "Tool Converter Demo":
					await _toolConverterExample.RunAsync();
					break;

				case "MCP Tools Discovery":
					await _mcpToolsExample.RunAsync();
					break;

				case "Sandbox Capabilities":
					await _sandboxCapabilitiesExample.RunAsync();
					break;

				case "Drift Detection":
					await _driftDetectionExample.RunAsync();
					break;

				case "Learnings Log":
					await _learningsLogExample.RunAsync();
					break;

				case "Budget & Health Tracking":
					await _observabilityBudgetExample.RunAsync();
					break;

				case "Meta-Harness Optimizer":
					await _optimizeExample.RunAsync();
					break;

				case "Skill Training (SkillOpt)":
					await _skillTrainingExample.RunAsync();
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
