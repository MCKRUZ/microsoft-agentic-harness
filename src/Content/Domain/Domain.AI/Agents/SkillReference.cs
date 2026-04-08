namespace Domain.AI.Agents;

/// <summary>
/// A reference to a skill from the Skills Table in AGENT.md.
/// Links an agent manifest to the skills it orchestrates.
/// </summary>
public class SkillReference
{
	/// <summary>
	/// Unique identifier for the skill (matches <see cref="Skills.SkillDefinition.Id"/>).
	/// </summary>
	public string Id { get; set; } = string.Empty;

	/// <summary>
	/// Display name of the skill.
	/// </summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// Brief description of what this skill does within the agent's workflow.
	/// </summary>
	public string Description { get; set; } = string.Empty;

	/// <summary>
	/// Relative path to the SKILL.md file from the agent's base directory.
	/// </summary>
	public string Path { get; set; } = string.Empty;

	/// <summary>
	/// Whether this skill is required for the agent to function.
	/// </summary>
	public bool IsRequired { get; set; } = true;

	/// <summary>
	/// The type of activity this skill performs (e.g., "research", "analysis", "generation").
	/// </summary>
	public string? ActivityType { get; set; }

	/// <summary>
	/// IDs of other skills that must complete before this one can execute.
	/// </summary>
	public IList<string> DependsOn { get; set; } = new List<string>();

	/// <summary>
	/// Whether this skill has a description.
	/// </summary>
	public bool HasDescription => !string.IsNullOrEmpty(Description);

	/// <summary>
	/// Whether this skill is optional (inverse of IsRequired).
	/// </summary>
	public bool IsOptional => !IsRequired;
}
