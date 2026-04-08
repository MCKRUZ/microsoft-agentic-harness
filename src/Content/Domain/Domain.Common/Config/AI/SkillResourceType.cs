namespace Domain.Common.Config.AI;

/// <summary>
/// Classifies the type of resource associated with a skill definition.
/// </summary>
public enum SkillResourceType
{
	/// <summary>
	/// A template file (*.template.md or *.template.json).
	/// </summary>
	Template,

	/// <summary>
	/// A reference file (*.reference.md or files in references/ folder).
	/// </summary>
	Reference,

	/// <summary>
	/// A script file (.py, .ps1, .sh, .bat) executed directly, never loaded into AI context.
	/// </summary>
	Script,

	/// <summary>
	/// An asset file (images, JSON schemas, binary resources) in assets/ folder.
	/// </summary>
	Asset,
}
