using Domain.AI.Agents;
using Domain.AI.Governance;

namespace Application.Core.CQRS.Autonomy;

/// <summary>
/// The effective autonomy tier for one subagent type, as resolved by
/// <see cref="Application.AI.Common.Interfaces.Governance.IAutonomyTierResolver"/> — profile
/// registry first, then the <c>PermissionsConfig.DefaultAutonomyLevel</c> fallback.
/// </summary>
/// <param name="SubagentType">The subagent type the tier was resolved for.</param>
/// <param name="Tier">The effective autonomy tier at read time. Pure config — reading it has no side effects.</param>
public sealed record AutonomyTierDetail(
    SubagentType SubagentType,
    AutonomyLevel Tier);
