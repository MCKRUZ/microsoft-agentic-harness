using System.Text.RegularExpressions;
using Domain.AI.Tools;
using Domain.Common.Workflow;

namespace Domain.AI.Agents;

/// <summary>
/// Agent manifest parsed from an AGENT.md file. Agents are top-level orchestrators
/// that define behavior, tools, workflow state, and the skills they coordinate.
/// </summary>
/// <remarks>
/// <para><b>AGENT.md Format:</b></para>
/// <code>
/// ---
/// name: "research-agent"
/// domain: "research"
/// version: "1.0.0"
/// category: "analysis"
/// tags: ["research", "file-analysis"]
/// allowed_tools: ["file_system", "github_repos"]
/// tools:
///   - name: "file_system"
///     operations: ["read", "search"]
/// ---
///
/// You are a research agent that finds and analyzes information...
/// </code>
/// </remarks>
public class AgentManifest
{
	#region Identification

	/// <summary>
	/// Unique identifier, typically derived from the folder name.
	/// </summary>
	public string Id { get; set; } = string.Empty;

	/// <summary>
	/// Display name of this agent.
	/// </summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// Description of this agent's purpose.
	/// </summary>
	public string Description { get; set; } = string.Empty;

	/// <summary>
	/// Semantic version of this agent definition.
	/// </summary>
	public string? Version { get; set; }

	/// <summary>
	/// Author of this agent definition.
	/// </summary>
	public string? Author { get; set; }

	/// <summary>
	/// Domain for this agent (e.g., "research", "orchestration").
	/// </summary>
	public string? Domain { get; set; }

	#endregion

	#region Categorization

	/// <summary>
	/// Category this agent belongs to (e.g., "analysis", "orchestration").
	/// </summary>
	public string? Category { get; set; }

	/// <summary>
	/// Tags for flexible categorization and discovery.
	/// </summary>
	public IList<string> Tags { get; set; } = new List<string>();

	#endregion

	#region Content

	/// <summary>
	/// Default instructions for this agent. Provides behavior for skills
	/// that don't specify their own instructions.
	/// </summary>
	public string? Instructions { get; set; }

	#endregion

	#region Tool Configuration

	/// <summary>
	/// Tools allowed for this agent. Skills can only use tools in this list.
	/// </summary>
	public IList<string>? AllowedTools { get; set; }

	/// <summary>
	/// Detailed tool declarations with operations, fallbacks, and conditions.
	/// </summary>
	public IList<ToolDeclaration>? ToolDeclarations { get; set; }

	#endregion

	#region Workflow Configuration

	/// <summary>
	/// State configuration defining valid statuses and transitions.
	/// </summary>
	public StateConfiguration? StateConfiguration { get; set; }

	/// <summary>
	/// Decision framework for validation and routing decisions.
	/// </summary>
	public DecisionFramework? DecisionFramework { get; set; }

	#endregion

	#region Skills Table

	/// <summary>
	/// Skill references that belong to this agent, parsed from the Skills Table.
	/// </summary>
	public IList<SkillReference> Skills { get; set; } = new List<SkillReference>();

	#endregion

	#region File System

	/// <summary>
	/// Physical file path to the AGENT.md file.
	/// </summary>
	public string FilePath { get; set; } = string.Empty;

	/// <summary>
	/// Base directory containing the AGENT.md and its skills.
	/// </summary>
	public string BaseDirectory { get; set; } = string.Empty;

	/// <summary>
	/// Timestamp when this manifest was last loaded.
	/// </summary>
	public DateTime LoadedAt { get; set; } = DateTime.UtcNow;

	/// <summary>
	/// File's last modified timestamp for cache invalidation.
	/// </summary>
	public DateTime LastModified { get; set; }

	#endregion

	#region Metadata

	/// <summary>
	/// Additional metadata from YAML frontmatter.
	/// </summary>
	public IDictionary<string, object>? Metadata { get; set; }

	#endregion

	#region Computed Properties

	public bool HasSkills => Skills.Count > 0;
	public bool HasToolRestrictions => AllowedTools?.Count > 0;
	public bool HasToolDeclarations => ToolDeclarations?.Count > 0;
	public bool HasStateConfiguration => StateConfiguration is not null;
	public bool HasDecisionFramework => DecisionFramework is not null;

	/// <summary>
	/// Extracts an ordinal number from the agent ID if present.
	/// Supports patterns like "phase0-discovery" -> 0, "step1-review" -> 1.
	/// Returns -1 if no ordinal is found.
	/// </summary>
	public int OrdinalNumber
	{
		get
		{
			var match = Regex.Match(Id, @"(?:phase|step|stage)(\d+)", RegexOptions.IgnoreCase);
			return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : -1;
		}
	}

	#endregion
}
