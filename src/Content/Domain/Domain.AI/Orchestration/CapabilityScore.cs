namespace Domain.AI.Orchestration;

/// <summary>
/// Breakdown of a candidate's scoring during capability matching.
/// Used for observability and debugging.
/// </summary>
public sealed record CapabilityScore
{
    /// <summary>The agent being scored.</summary>
    public required string AgentId { get; init; }

    /// <summary>Ratio of required tools the agent has (0.0 to 1.0).</summary>
    public required double ToolCoverage { get; init; }

    /// <summary>How well the agent type matches the task category (0.0 to 1.0).</summary>
    public required double TypeAlignment { get; init; }

    /// <summary>How much tier headroom above the minimum (0.0 to 1.0).</summary>
    public required double TierHeadroom { get; init; }

    /// <summary>Weighted composite of the three factors.</summary>
    public required double TotalScore { get; init; }
}
