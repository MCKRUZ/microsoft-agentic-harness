namespace Domain.AI.DriftDetection;

/// <summary>
/// Hierarchy level at which a drift baseline is defined.
/// Baselines cascade: TaskType -> Skill -> Agent (most specific wins).
/// </summary>
public enum DriftScope
{
    /// <summary>Agent-wide baseline (broadest scope).</summary>
    Agent = 0,
    /// <summary>Skill-specific baseline.</summary>
    Skill = 1,
    /// <summary>Task-type-specific baseline (most granular).</summary>
    TaskType = 2
}
