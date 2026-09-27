using Domain.AI.Agents;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Agents.RefreshAgentRegistry;

/// <summary>
/// Forces the agent registry to rescan its configured filesystem paths immediately, returning a
/// summary of what changed. The operator-triggered half of issue #705's "no restart to add/remove/
/// refresh an agent" fix — the other half is <c>AgentManifestWatcherService</c>, which does the same
/// underlying reload automatically on a filesystem change.
/// </summary>
/// <remarks>
/// An operator forcing a rescan is a legitimate, low-risk action — unlike drift baseline
/// recalculation, it re-anchors nothing security-sensitive, it only reads whatever <c>AGENT.md</c>
/// files are already on disk. It is still recorded in the audit trail (via <c>IAuditSink</c>, the
/// same general-purpose sink structured logging and compliance tooling read from) so "who reloaded
/// the agent set, and when" is answerable, but — unlike the drift write commands — that record is
/// best-effort rather than a precondition for the reload: refusing an operational cache refresh
/// because a log write failed would make the audit trail a liability instead of a safeguard.
/// </remarks>
public sealed record RefreshAgentRegistryCommand : IRequest<Result<AgentRegistryRefreshResult>>
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
