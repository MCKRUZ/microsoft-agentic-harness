namespace Domain.AI.Skills;

/// <summary>
/// Three-tier context loading configuration for a skill.
/// Controls what context loads at each tier: always, activity-specific, or on-demand.
/// </summary>
/// <remarks>
/// Parsed from the "context_loading" section in SKILL.md YAML frontmatter.
/// <code>
/// context_loading:
///   tier1:
///     required: true
///     files: [context/org-summary.md]
///   tier2:
///     required: true
///     files: [inputs/extracts/project-brief-extract.md]
///     from_dependencies: [discovery-intake.md]
///     max_tokens: 8000
///   tier3:
///     lookup_paths: [inputs/raw/rfps/]
///     fallback_prompt: "Use file tools to access source documents if needed."
/// </code>
/// </remarks>
public class ContextLoading
{
	/// <summary>
	/// Tier 1 (organizational/strategic context) — always loaded.
	/// </summary>
	public ContextTierConfig? Tier1 { get; set; }

	/// <summary>
	/// Tier 2 (domain/activity-specific context) — loaded on demand.
	/// </summary>
	public ContextTierConfig? Tier2 { get; set; }

	/// <summary>
	/// Tier 3 (on-demand lookup paths) — accessed via tools, not pre-loaded.
	/// </summary>
	public ContextTierConfig? Tier3 { get; set; }

	public bool HasConfiguration => Tier1 is not null || Tier2 is not null || Tier3 is not null;
	public bool RequiresTier1 => Tier1?.Required ?? false;
	public bool RequiresTier2 => Tier2?.Required ?? false;
	public bool HasTier3 => Tier3 is not null;
}

/// <summary>
/// Configuration for a single tier of context loading.
/// </summary>
public class ContextTierConfig
{
	/// <summary>
	/// Whether this tier is required for skill execution.
	/// </summary>
	public bool Required { get; set; }

	/// <summary>
	/// Files to load at this tier.
	/// </summary>
	public IList<string> Files { get; set; } = new List<string>();

	/// <summary>
	/// Files to pull from completed dependency activities.
	/// </summary>
	public IList<string> FromDependencies { get; set; } = new List<string>();

	/// <summary>
	/// Paths available for on-demand file lookup (Tier 3).
	/// </summary>
	public IList<string> LookupPaths { get; set; } = new List<string>();

	/// <summary>
	/// Prompt to give the agent when it needs to access Tier 3 resources.
	/// </summary>
	public string? FallbackPrompt { get; set; }

	/// <summary>
	/// Maximum token budget for this tier.
	/// </summary>
	public int? MaxTokens { get; set; }
}
