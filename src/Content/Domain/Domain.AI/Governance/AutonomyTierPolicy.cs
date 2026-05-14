using Domain.AI.Permissions;

namespace Domain.AI.Governance;

/// <summary>
/// Maps an autonomy level to its permission behavior and per-tool overrides.
/// Pure value object with no behavior.
/// </summary>
public sealed record AutonomyTierPolicy
{
    /// <summary>Which tier this policy applies to.</summary>
    public required AutonomyLevel Level { get; init; }

    /// <summary>
    /// The baseline permission behavior for this tier.
    /// Restricted and Supervised map to Ask; Autonomous maps to Allow.
    /// </summary>
    public required PermissionBehaviorType DefaultBehavior { get; init; }

    /// <summary>
    /// Per-tool behavior overrides within the tier. For example, a Restricted agent
    /// might still Allow <c>"query_knowledge_graph"</c>. Null means no overrides.
    /// </summary>
    public IReadOnlyDictionary<string, PermissionBehaviorType>? ToolOverrides { get; init; }
}
