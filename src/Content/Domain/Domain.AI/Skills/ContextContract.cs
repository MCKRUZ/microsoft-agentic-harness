namespace Domain.AI.Skills;

/// <summary>
/// Defines the input/output contract for a skill, enabling validation
/// that required context is available before execution.
/// </summary>
/// <remarks>
/// Parsed from the "context_contract" section in SKILL.md YAML frontmatter.
/// <code>
/// context_contract:
///   required_inputs:
///     - project_brief.md
///   optional_inputs:
///     - previous_report.md
///   produces:
///     - feasibility_report.md
///   dependencies:
///     - stakeholder_interview_activity
/// </code>
/// </remarks>
public class ContextContract
{
	/// <summary>
	/// Files that must be present before the skill can execute.
	/// </summary>
	public IList<string> RequiredInputs { get; set; } = new List<string>();

	/// <summary>
	/// Files that enhance results if present but aren't required.
	/// </summary>
	public IList<string> OptionalInputs { get; set; } = new List<string>();

	/// <summary>
	/// Artifacts this skill produces.
	/// </summary>
	public IList<string> Produces { get; set; } = new List<string>();

	/// <summary>
	/// Other skills/activities that must complete before this one.
	/// </summary>
	public IList<string> Dependencies { get; set; } = new List<string>();

	public bool HasRequiredInputs => RequiredInputs.Count > 0;
	public bool HasOptionalInputs => OptionalInputs.Count > 0;
	public bool HasOutputs => Produces.Count > 0;
	public bool HasDependencies => Dependencies.Count > 0;
	public int TotalInputCount => RequiredInputs.Count + OptionalInputs.Count;
	public bool HasAnyRequirements => HasRequiredInputs || HasDependencies;
}
