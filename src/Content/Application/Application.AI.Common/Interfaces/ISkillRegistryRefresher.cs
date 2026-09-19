using Domain.AI.Skills;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Lets a caller force <see cref="ISkillMetadataRegistry"/> to pick up filesystem changes
/// without a process restart (issue #709, mirroring <see cref="IAgentRegistryRefresher"/> from
/// issue #705).
/// </summary>
/// <remarks>
/// Deliberately a separate, narrow interface rather than additional members on
/// <see cref="ISkillMetadataRegistry"/>: the read interface has many consumers, while this reload
/// seam has exactly one production consumer (the operator refresh command) and one implementation
/// (<c>SkillMetadataRegistry</c>). Both members are implemented by the same singleton instance
/// <see cref="ISkillMetadataRegistry"/> resolves to, so calling either one is immediately visible to
/// every reader of the registry.
/// </remarks>
public interface ISkillRegistryRefresher
{
    /// <summary>
    /// Drops the cached skill set so the next read re-scans the filesystem. Cheap and idempotent —
    /// safe to call repeatedly for the same underlying change (e.g. a burst of filesystem events),
    /// since the actual rescan is deferred to whichever read happens first afterward.
    /// </summary>
    void Invalidate();

    /// <summary>
    /// Rescans the filesystem immediately and swaps in the result, returning a summary of what
    /// changed compared to the previous cached set. Unlike <see cref="Invalidate"/>, this does the
    /// rescan synchronously so an operator triggering it gets a concrete answer about what changed,
    /// rather than having to re-list skills afterward to find out.
    /// </summary>
    /// <returns>What changed: added, updated, and removed skill ids, the new total, and the searched paths.</returns>
    SkillRegistryRefreshResult Refresh();
}
