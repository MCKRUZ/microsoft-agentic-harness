using Domain.AI.Skills;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Contract for loading and managing skills from SKILL.md files.
/// Supports partial load (metadata-only discovery) and full load (with instructions and resources).
/// </summary>
public interface ISkillLoaderService
{
	#region Core Loading

	/// <summary>
	/// Loads a skill fully by its identifier.
	/// </summary>
	Task<SkillDefinition> LoadSkillAsync(string skillId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Loads a SKILL.md file from a specific file path.
	/// </summary>
	Task<SkillDefinition> LoadSkillFileFromPathAsync(string filePath, CancellationToken cancellationToken = default);

	/// <summary>
	/// Attempts to load a skill, returning null if not found.
	/// </summary>
	Task<SkillDefinition?> TryLoadSkillAsync(string skillId, CancellationToken cancellationToken = default);

	#endregion

	#region Discovery

	/// <summary>
	/// Discovers all available skill IDs.
	/// </summary>
	Task<IReadOnlyList<string>> DiscoverSkillIdsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Discovers skills matching an optional filter (metadata only, not fully loaded).
	/// </summary>
	Task<IReadOnlyList<SkillDefinition>> DiscoverSkillsAsync(Func<SkillDefinition, bool>? filter = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Discovers skills by category (metadata only).
	/// </summary>
	Task<IReadOnlyList<SkillDefinition>> DiscoverByCategoryAsync(string category, CancellationToken cancellationToken = default);

	/// <summary>
	/// Discovers skills with any of the specified tags (metadata only).
	/// </summary>
	Task<IReadOnlyList<SkillDefinition>> DiscoverByTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets all unique categories across all skills.
	/// </summary>
	Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets all unique tags across all skills.
	/// </summary>
	Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks if a skill exists.
	/// </summary>
	bool SkillExists(string skillId);

	/// <summary>
	/// Asynchronously checks if a skill exists.
	/// </summary>
	Task<bool> SkillExistsAsync(string skillId, CancellationToken cancellationToken = default);

	#endregion

	#region Resource Loading

	/// <summary>
	/// Loads a specific template for a skill. Returns null if not found.
	/// </summary>
	Task<string?> TryLoadTemplateAsync(string skillId, string templateName, CancellationToken cancellationToken = default);

	/// <summary>
	/// Loads a specific template for a skill. Throws if not found.
	/// </summary>
	Task<string> LoadTemplateAsync(string skillId, string templateName, CancellationToken cancellationToken = default);

	/// <summary>
	/// Loads a specific reference file for a skill.
	/// </summary>
	Task<string> LoadReferenceAsync(string skillId, string referenceName, CancellationToken cancellationToken = default);

	/// <summary>
	/// Loads all templates for a skill.
	/// </summary>
	Task<IDictionary<string, string>> LoadAllTemplatesAsync(string skillId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Loads all references for a skill.
	/// </summary>
	Task<IDictionary<string, string>> LoadAllReferencesAsync(string skillId, CancellationToken cancellationToken = default);

	#endregion

	#region Cache Management

	/// <summary>
	/// Clears the entire skill cache.
	/// </summary>
	void ClearCache();

	/// <summary>
	/// Clears a specific skill from the cache.
	/// </summary>
	void ClearFromCache(string skillId);

	/// <summary>
	/// Preloads all skills into the cache.
	/// </summary>
	Task<int> PreloadAllSkillsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets cache statistics.
	/// </summary>
	SkillCacheStatistics GetCacheStatistics();

	#endregion

	#region File Watching

	/// <summary>
	/// Starts watching for skill file changes (hot reload).
	/// </summary>
	void StartWatching();

	/// <summary>
	/// Stops watching for file changes.
	/// </summary>
	void StopWatching();

	/// <summary>
	/// Whether file watching is active.
	/// </summary>
	bool IsWatching { get; }

	/// <summary>
	/// Raised when a skill file changes.
	/// </summary>
	event EventHandler<SkillChangedEventArgs>? SkillChanged;

	#endregion
}
