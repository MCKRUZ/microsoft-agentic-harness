using Domain.AI.Permissions;

namespace Domain.AI.Governance;

/// <summary>
/// Helpers projecting an <see cref="AutonomyLevel"/> onto the permission model.
/// </summary>
public static class AutonomyLevelExtensions
{
    /// <summary>
    /// Maps an autonomy tier to the default permission behavior it grants: <see cref="AutonomyLevel.Autonomous"/>
    /// acts within guardrails so its baseline is <see cref="PermissionBehaviorType.Allow"/>; every lower tier
    /// (<see cref="AutonomyLevel.Supervised"/>, <see cref="AutonomyLevel.Restricted"/>) recommends-and-waits, so
    /// its baseline is <see cref="PermissionBehaviorType.Ask"/>.
    /// </summary>
    /// <remarks>
    /// This is the single source of the "Autonomous grants Allow, everything else asks" rule, shared by every
    /// permission-rule provider (autonomy-tier, plugin boundary, capability envelope) so the tier-to-behavior
    /// policy cannot drift between them. Differentiation <em>within</em> the Ask tiers is expressed through
    /// per-tool overrides in tier-policy configuration, not here.
    /// </remarks>
    /// <param name="level">The autonomy tier.</param>
    /// <returns>The baseline permission behavior for the tier.</returns>
    public static PermissionBehaviorType ToDefaultPermissionBehavior(this AutonomyLevel level) =>
        level == AutonomyLevel.Autonomous ? PermissionBehaviorType.Allow : PermissionBehaviorType.Ask;
}
