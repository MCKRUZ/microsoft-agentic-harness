using Domain.AI.Skills;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Skills.RefreshSkillRegistry;

/// <summary>
/// Forces the skill registry to rescan its configured filesystem paths immediately, returning a
/// summary of what changed. The operator-triggered half of issue #709's "no restart to add/remove/
/// refresh a skill" fix (mirroring <c>Application.Core.CQRS.Agents.RefreshAgentRegistry
/// .RefreshAgentRegistryCommand</c> from issue #705) — the other half is
/// <c>SkillManifestWatcherService</c>, which does the same underlying reload automatically on a
/// filesystem change.
/// </summary>
/// <remarks>
/// An operator forcing a rescan is a legitimate, low-risk action — it re-anchors nothing
/// security-sensitive, it only reads whatever <c>SKILL.md</c> files are already on disk. It is still
/// recorded in the audit trail (via <c>IAuditSink</c>, the same general-purpose sink structured
/// logging and compliance tooling read from) so "who reloaded the skill set, and when" is
/// answerable, but that record is best-effort rather than a precondition for the reload: refusing an
/// operational cache refresh because a log write failed would make the audit trail a liability
/// instead of a safeguard.
/// </remarks>
public sealed record RefreshSkillRegistryCommand : IRequest<Result<SkillRegistryRefreshResult>>
{
    /// <summary>
    /// The requesting caller's identity, recorded in the audit trail.
    /// </summary>
    /// <remarks>
    /// <b>Populated exclusively by the controller from the authenticated principal's stable identity
    /// claim</b> (<c>ClaimsPrincipalExtensions.GetUserIdOrNull</c>). It must never be bound from a
    /// request body, query string, or header.
    /// </remarks>
    public required string CallerId { get; init; }
}
