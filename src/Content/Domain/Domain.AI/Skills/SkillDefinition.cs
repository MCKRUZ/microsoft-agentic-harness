using Domain.AI.Tools;
using Domain.Common.Workflow;
using Microsoft.Extensions.AI;

namespace Domain.AI.Skills;

/// <summary>
/// Skill definition loaded from a SKILL.md file. Skills are self-contained units
/// that define agent behavior, instructions, tools, and resources for specific tasks.
/// </summary>
/// <remarks>
/// <para><b>Progressive Disclosure Architecture (3-level loading):</b></para>
///
/// <para><b>Level 1 — Index Card (Metadata):</b> ~100 tokens.
/// Id, Name, Description, Category, Tags. Always loaded at startup.
/// The agent knows "I have a skill for X" without holding procedures in memory.</para>
///
/// <para><b>Level 2 — Folder (Instructions):</b> ~5,000 tokens recommended.
/// Full SKILL.md body loads on demand. Procedural knowledge: "here's how we do X."</para>
///
/// <para><b>Level 3 — Filing Cabinet (Resources):</b> Unlimited.
/// Templates, references, scripts, assets. Scripts execute without loading into context.
/// References pulled only when needed. Effective knowledge is unbounded.</para>
/// </remarks>
public class SkillDefinition
{
	#region Level 1: Index Card (Metadata — Always Loaded)

	/// <summary>
	/// Unique identifier derived from the skill's relative path (e.g., "agents/research").
	/// </summary>
	public string Id { get; set; } = string.Empty;

	/// <summary>
	/// Display name from YAML frontmatter.
	/// </summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// Concise description (~50-100 tokens) for discovery.
	/// </summary>
	public string Description { get; set; } = string.Empty;

	#endregion

	#region Level 2: Folder (Instructions — On Demand)

	/// <summary>
	/// Full instruction content (markdown body after frontmatter).
	/// Becomes the agent's system prompt. Target: ~5,000 tokens.
	/// </summary>
	public string? Instructions { get; set; } = string.Empty;

	#endregion

	#region Categorization

	/// <summary>
	/// Version of this skill definition.
	/// </summary>
	public string? Version { get; set; }

	/// <summary>
	/// Author of this skill.
	/// </summary>
	public string? Author { get; set; }

	/// <summary>
	/// Primary category (e.g., "research", "analysis", "orchestration").
	/// </summary>
	public string? Category { get; set; }

	/// <summary>
	/// Skill type that defines its role (e.g., "research", "generation", "analysis", "orchestration").
	/// </summary>
	public string? SkillType { get; set; }

	/// <summary>
	/// Tags for multi-dimensional filtering.
	/// </summary>
	public IList<string> Tags { get; set; } = new List<string>();

	#endregion

	#region Runtime Configuration

	/// <summary>
	/// Tools allowed when this skill is active. Null or empty = no restrictions.
	/// </summary>
	public IList<string>? AllowedTools { get; set; }

	/// <summary>
	/// Model deployment override. Null = use default from config.
	/// </summary>
	public string? ModelOverride { get; set; }

	/// <summary>
	/// AI Foundry persistent agent ID for PersistentAgents framework type.
	/// </summary>
	public string? AgentId { get; set; }

	/// <summary>
	/// License identifier (e.g., "MIT", "Apache-2.0", "Proprietary").
	/// </summary>
	public string? License { get; set; }

	#endregion

	#region File System

	/// <summary>
	/// Physical file path to the SKILL.md file.
	/// </summary>
	public string FilePath { get; set; } = string.Empty;

	/// <summary>
	/// Base directory containing the skill and its resources.
	/// </summary>
	public string BaseDirectory { get; set; } = string.Empty;

	/// <summary>
	/// Timestamp when the skill was last loaded.
	/// </summary>
	public DateTime LoadedAt { get; set; } = DateTime.UtcNow;

	/// <summary>
	/// File's last modified timestamp for cache invalidation.
	/// </summary>
	public DateTime LastModified { get; set; }

	#endregion

	#region Level 3: Filing Cabinet (Resources — On Demand)

	/// <summary>
	/// Template files in the skill's templates/ subdirectory.
	/// </summary>
	public IList<SkillResource> Templates { get; set; } = new List<SkillResource>();

	/// <summary>
	/// Reference files (*.reference.md or files in references/ subdirectory).
	/// </summary>
	public IList<SkillResource> References { get; set; } = new List<SkillResource>();

	/// <summary>
	/// Script files (.py, .ps1, .sh) — executed directly, never loaded into AI context.
	/// </summary>
	public IList<SkillResource> Scripts { get; set; } = new List<SkillResource>();

	/// <summary>
	/// Asset files (images, JSON schemas, binary resources).
	/// </summary>
	public IList<SkillResource> Assets { get; set; } = new List<SkillResource>();

	#endregion

	#region Extensibility

	/// <summary>
	/// Additional metadata from YAML frontmatter for domain-specific properties.
	/// </summary>
	public IDictionary<string, object>? Metadata { get; set; }

	/// <summary>
	/// Context contract defining required/optional inputs, outputs, and dependencies.
	/// </summary>
	public ContextContract? ContextContract { get; set; }

	/// <summary>
	/// Three-tier context loading configuration.
	/// </summary>
	public ContextLoading? ContextLoading { get; set; }

	/// <summary>
	/// Pre-created AI tools available to agents using this skill.
	/// </summary>
	public IList<AITool>? Tools { get; set; }

	/// <summary>
	/// State configuration for workflow tracking.
	/// </summary>
	public StateConfiguration? StateConfiguration { get; set; }

	/// <summary>
	/// Decision framework for validation/routing decisions.
	/// </summary>
	public DecisionFramework? DecisionFramework { get; set; }

	/// <summary>
	/// Detailed tool declarations from YAML frontmatter.
	/// </summary>
	public IList<ToolDeclaration>? ToolDeclarations { get; set; }

	#endregion

	#region Hierarchy

	/// <summary>
	/// Child skills for hierarchical organization.
	/// </summary>
	public IList<SkillDefinition>? Children { get; set; }

	/// <summary>
	/// Parent skill ID if this is a child skill.
	/// </summary>
	public string? ParentId { get; set; }

	#endregion

	#region Loading State

	/// <summary>
	/// Whether this skill has been fully loaded (including Instructions).
	/// When false, only Level 1 metadata is populated.
	/// </summary>
	public bool IsFullyLoaded { get; set; }

	#endregion

	#region Computed Properties

	public bool HasTemplates => Templates.Count > 0;
	public bool HasReferences => References.Count > 0;
	public bool HasScripts => Scripts?.Count > 0;
	public bool HasAssets => Assets?.Count > 0;
	public bool HasChildren => Children?.Count > 0;
	public bool IsChild => !string.IsNullOrEmpty(ParentId);
	public bool HasTags => Tags.Count > 0;
	public bool HasToolDeclarations => ToolDeclarations?.Count > 0;
	public bool HasContextContract => ContextContract is not null && ContextContract.HasAnyRequirements;
	public bool HasContextLoading => ContextLoading is not null && ContextLoading.HasConfiguration;
	public bool HasToolRestrictions => AllowedTools?.Count > 0;
	public bool HasSkillType => !string.IsNullOrEmpty(SkillType);
	public bool HasModelOverride => !string.IsNullOrEmpty(ModelOverride);
	public bool HasPersistentAgentId => !string.IsNullOrEmpty(AgentId);
	public bool HasLicense => !string.IsNullOrEmpty(License);
	public int TotalResourceCount =>
		(Templates?.Count ?? 0) + (References?.Count ?? 0) +
		(Scripts?.Count ?? 0) + (Assets?.Count ?? 0);

	#endregion

	#region Progressive Disclosure Metrics

	/// <summary>
	/// Estimated tokens for Level 1 (metadata). Target: ~100 per skill.
	/// </summary>
	public int Level1TokenEstimate
	{
		get
		{
			var count = EstimateTokens(Id) + EstimateTokens(Name) + EstimateTokens(Description);
			count += EstimateTokens(Category ?? string.Empty);
			count += Tags.Sum(EstimateTokens);
			return count;
		}
	}

	/// <summary>
	/// Estimated tokens for Level 2 (instructions). Target: ~5,000.
	/// </summary>
	public int Level2TokenEstimate => EstimateTokens(Instructions ?? string.Empty);

	/// <summary>
	/// Estimated tokens for loaded Level 3 resources (excludes scripts).
	/// </summary>
	public int Level3LoadedTokenEstimate
	{
		get
		{
			var count = 0;
			foreach (var t in Templates.Where(r => r.IsLoaded))
				count += EstimateTokens(t.Content ?? string.Empty);
			foreach (var r in References.Where(r => r.IsLoaded))
				count += EstimateTokens(r.Content ?? string.Empty);
			foreach (var a in Assets.Where(r => r.IsLoaded))
				count += EstimateTokens(a.Content ?? string.Empty);
			return count;
		}
	}

	/// <summary>
	/// Total estimated tokens currently loaded across all levels.
	/// </summary>
	public int TotalLoadedTokenEstimate => Level1TokenEstimate + Level2TokenEstimate + Level3LoadedTokenEstimate;

	/// <summary>
	/// Whether Level 2 exceeds the recommended 5,000 token budget.
	/// </summary>
	public bool IsLevel2Oversized => Level2TokenEstimate > 5000;

	private static int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

	#endregion
}
