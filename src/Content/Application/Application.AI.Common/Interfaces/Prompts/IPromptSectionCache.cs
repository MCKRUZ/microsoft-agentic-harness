using Domain.AI.Prompts;

namespace Application.AI.Common.Interfaces.Prompts;

/// <summary>
/// Cache for computed system prompt sections. Supports per-type invalidation
/// to enable targeted cache clearing (e.g., invalidate SkillInstructions when
/// skills change, without recomputing AgentIdentity).
/// </summary>
public interface IPromptSectionCache
{
    /// <summary>Attempts to retrieve a cached section.</summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="type">The section type to look up.</param>
    /// <param name="section">The cached section, if found.</param>
    /// <returns><c>true</c> if a cached section was found; otherwise <c>false</c>.</returns>
    bool TryGet(string agentId, SystemPromptSectionType type, out SystemPromptSection? section);

    /// <summary>Stores a section in the cache.</summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="section">The section to cache.</param>
    void Set(string agentId, SystemPromptSection section);

    /// <summary>Invalidates all cached entries of the specified section type.</summary>
    /// <param name="type">The section type to invalidate.</param>
    void Invalidate(SystemPromptSectionType type);

    /// <summary>Invalidates all cached entries.</summary>
    void InvalidateAll();
}
