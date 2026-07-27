using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Autonomy;

/// <summary>
/// Reads the effective autonomy tier for a subagent type. Strictly read-only: autonomy tiers
/// are pure configuration (profile registry plus <c>PermissionsConfig</c> fallback), so this
/// query performs no writes, no audit records, and no state changes.
/// </summary>
/// <remarks>
/// The subagent type travels as a string because the HTTP surface receives it as a route
/// segment; the handler parses it by name and returns <c>NotFound</c> for anything that does
/// not name a defined <see cref="Domain.AI.Agents.SubagentType"/> member.
/// </remarks>
public sealed record GetAutonomyTierQuery : IRequest<Result<AutonomyTierDetail>>
{
    /// <summary>The subagent type name to resolve (case-insensitive, e.g. <c>Explore</c>).</summary>
    public required string SubagentType { get; init; }
}
