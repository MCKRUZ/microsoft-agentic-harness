using System.Reflection;
using Domain.AI.Agents;

namespace Application.Core.Agents;

/// <summary>
/// Static factory for creating well-known agent execution contexts.
/// Instructions are loaded from embedded SKILL.md files, keeping agent behavior
/// externalized and editable without recompilation.
/// </summary>
public static class AgentDefinitions
{
	private const string ResourcePrefix = "Application.Core.Agents.Skills.";

	/// <summary>
	/// Creates a standalone research agent that finds and analyzes information
	/// using file system tools, MCP tools, and external connectors.
	/// </summary>
	public static AgentExecutionContext CreateResearchAgent(string? deploymentName = null)
	{
		return new AgentExecutionContext
		{
			Name = "ResearchAgent",
			Description = "Finds, reads, and analyzes information from files, repositories, and external sources.",
			Instruction = LoadSkillInstructions("research_agent.SKILL.md"),
			DeploymentName = deploymentName,
			AdditionalProperties = new Dictionary<string, object>
			{
				["agentType"] = "standalone",
				["category"] = "research"
			}
		};
	}

	/// <summary>
	/// Creates an orchestrator agent that decomposes complex tasks and
	/// delegates subtasks to specialized sub-agents.
	/// </summary>
	public static AgentExecutionContext CreateOrchestratorAgent(
		IEnumerable<string> availableAgentNames,
		string? deploymentName = null)
	{
		var agentList = string.Join("\n", availableAgentNames.Select(n => $"- {n}"));
		var baseInstructions = LoadSkillInstructions("orchestrator_agent.SKILL.md");

		// Inject available agents into the instructions
		var instructions = $"""
			{baseInstructions}

			## Available Agents
			{agentList}
			""";

		return new AgentExecutionContext
		{
			Name = "OrchestratorAgent",
			Description = "Coordinates specialized agents to accomplish complex, multi-step tasks.",
			Instruction = instructions,
			DeploymentName = deploymentName,
			AdditionalProperties = new Dictionary<string, object>
			{
				["agentType"] = "orchestrator",
				["category"] = "orchestration"
			}
		};
	}

	/// <summary>
	/// Loads the instruction body from an embedded SKILL.md resource.
	/// Strips YAML frontmatter (everything between --- delimiters) and returns the markdown body.
	/// </summary>
	private static string LoadSkillInstructions(string resourceName)
	{
		var assembly = Assembly.GetExecutingAssembly();
		var fullName = ResourcePrefix + resourceName;

		using var stream = assembly.GetManifestResourceStream(fullName)
			?? throw new InvalidOperationException(
				$"Embedded resource '{fullName}' not found. " +
				$"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

		using var reader = new StreamReader(stream);
		var content = reader.ReadToEnd();

		return StripFrontmatter(content);
	}

	/// <summary>
	/// Strips YAML frontmatter delimited by --- from markdown content.
	/// </summary>
	private static string StripFrontmatter(string content)
	{
		if (!content.StartsWith("---"))
			return content.Trim();

		var endIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
		if (endIndex < 0)
			return content.Trim();

		return content[(endIndex + 3)..].Trim();
	}
}
