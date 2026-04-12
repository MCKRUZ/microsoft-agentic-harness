using Domain.AI.Skills;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Registry that discovers and caches skill metadata from filesystem SKILL.md files.
/// Provides metadata-only access (id, name, description, tags, allowed-tools) without
/// loading full skill content — content is provided at runtime by <c>FileAgentSkillsProvider</c>.
/// </summary>
/// <remarks>
/// This replaces <c>ISkillLoaderService</c>. Full progressive skill disclosure (Tier 2 body,
/// Tier 3 resources) is handled by the framework's <c>FileAgentSkillsProvider</c> AIContextProvider.
/// </remarks>
public interface ISkillMetadataRegistry
{
    /// <summary>
    /// Returns all discovered skill definitions (metadata only — no body content).
    /// </summary>
    IReadOnlyList<SkillDefinition> GetAll();

    /// <summary>
    /// Returns the skill definition for the given ID, or null if not found.
    /// </summary>
    SkillDefinition? TryGet(string skillId);

    /// <summary>
    /// Discovers skills matching a category filter.
    /// </summary>
    IReadOnlyList<SkillDefinition> GetByCategory(string category);

    /// <summary>
    /// Discovers skills that have any of the specified tags.
    /// </summary>
    IReadOnlyList<SkillDefinition> GetByTags(IEnumerable<string> tags);

    /// <summary>
    /// Returns skills matching the given skill type (e.g., "orchestration", "analysis").
    /// </summary>
    IReadOnlyList<SkillDefinition> GetBySkillType(string skillType);

    /// <summary>
    /// Returns the filesystem paths that were searched during discovery.
    /// </summary>
    IReadOnlyList<string> SearchedPaths { get; }
}
