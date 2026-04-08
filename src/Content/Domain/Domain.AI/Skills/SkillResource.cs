using static Domain.Common.Config.AI.SkillResourceType;

namespace Domain.AI.Skills;

/// <summary>
/// Represents a resource file associated with a skill definition.
/// Resources include templates, references, scripts, and assets loaded on demand (Level 3).
/// </summary>
public class SkillResource
{
	/// <summary>
	/// The file name (e.g., "output.template.md").
	/// </summary>
	public string FileName { get; set; } = string.Empty;

	/// <summary>
	/// The absolute file path on disk.
	/// </summary>
	public string FilePath { get; set; } = string.Empty;

	/// <summary>
	/// The path relative to the skill's base directory.
	/// </summary>
	public string RelativePath { get; set; } = string.Empty;

	/// <summary>
	/// The type of resource (Template, Reference, Script, Asset).
	/// </summary>
	public Domain.Common.Config.AI.SkillResourceType ResourceType { get; set; }

	/// <summary>
	/// The resource content, populated only when explicitly loaded.
	/// Scripts execute directly and should never have content loaded into AI context.
	/// </summary>
	public string? Content { get; set; }

	/// <summary>
	/// Whether the content has been loaded into memory.
	/// </summary>
	public bool IsLoaded => Content is not null;
}
