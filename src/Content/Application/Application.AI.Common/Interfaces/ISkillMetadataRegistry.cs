using Domain.AI.Skills;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Registry that discovers and caches skill metadata from filesystem SKILL.md files.
/// Provides metadata-only access (id, name, description, tags, allowed-tools) without
/// loading full skill content — content is provided at runtime by <c>AgentSkillsProvider</c>.
/// </summary>
/// <remarks>
/// This replaces <c>ISkillLoaderService</c>. Full progressive skill disclosure (Tier 2 body,
/// Tier 3 resources) is handled by the framework's <c>AgentSkillsProvider</c> AIContextProvider.
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

    /// <summary>
    /// Monotonically increasing generation counter. Incremented both when the underlying cache is
    /// successfully rebuilt (whether or not the rebuild found any actual differences) AND when
    /// <c>Invalidate</c> marks it stale, before any rebuild has happened.
    /// </summary>
    /// <remarks>
    /// Exists for a consumer that derives and caches a value FROM this registry's contents per some
    /// other key (for example <c>SkillManifestEgressPolicyResolver</c> caching a per-skill egress
    /// policy, or <c>PluginPermissionRuleProvider</c> caching plugin-derived tool rules) and needs a
    /// cheap way to detect "the underlying skill data may have changed" without this registry needing
    /// to know who is watching, or without re-deriving change detection itself (security-review
    /// finding on issue #709: before this existed, a hot-reloaded skill's egress allowlist could be
    /// narrowed or revoked and a consumer's stale cached policy would keep enforcing the old, broader
    /// one until process restart). Deliberately incremented unconditionally on every rebuild, not only
    /// when a diff was detected — a consumer relying on it for a security-relevant decision must never
    /// have to trust this registry's own best-effort added/updated/removed classification to stay safe.
    /// <para>
    /// <b>Also incremented by <c>Invalidate</c> itself, not only by a completed rebuild</b> (CI
    /// correctness-review finding, same issue): a Version-checking consumer is specifically designed
    /// to skip calling back into this registry on a cache hit, so if only a completed rebuild advanced
    /// Version, an automatic watcher-driven <c>Invalidate</c> — which does not itself rebuild anything
    /// — would leave Version frozen until some UNRELATED caller happened to read this registry first.
    /// Bumping it at invalidation time closes that gap: a consumer notices the change immediately,
    /// clears its own cache, and calls back into this registry, which is what actually triggers the
    /// lazy rebuild (and one further, harmless Version bump).
    /// </para>
    /// </remarks>
    long Version { get; }
}
