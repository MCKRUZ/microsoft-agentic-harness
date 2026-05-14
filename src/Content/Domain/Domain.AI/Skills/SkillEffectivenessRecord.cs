namespace Domain.AI.Skills;

/// <summary>
/// Tracks how effective a skill is for a given query classification.
/// Stored as graph nodes, queried to inform skill selection.
/// </summary>
public record SkillEffectivenessRecord
{
    /// <summary>The skill being tracked.</summary>
    public required string SkillId { get; init; }
    /// <summary>The query classification (e.g., "factual", "analytical").</summary>
    public required string QueryClassification { get; init; }
    /// <summary>Number of successful outcomes.</summary>
    public required int SuccessCount { get; init; }
    /// <summary>Total number of outcomes recorded.</summary>
    public required int TotalCount { get; init; }
    /// <summary>Average quality score across all outcomes (0.0-1.0).</summary>
    public required double AverageQuality { get; init; }
    /// <summary>Computed success rate.</summary>
    public double SuccessRate => TotalCount > 0 ? (double)SuccessCount / TotalCount : 0;
}
