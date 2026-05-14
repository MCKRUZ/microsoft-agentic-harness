namespace Domain.Common.Config.AI.Orchestration;

/// <summary>
/// Configurable weights for the capability match scoring algorithm.
/// Bound from <c>AppConfig:AI:Orchestration:Subagent:CapabilityMatchWeights</c>.
/// </summary>
/// <remarks>
/// Weights are normalized at consumption time by <c>CapabilityMatchStrategy</c>.
/// If configured values don't sum to 1.0, each is divided by the total to prevent
/// scores exceeding 1.0.
/// </remarks>
public class CapabilityMatchWeightsConfig
{
    /// <summary>Weight for tool coverage factor (default 0.4). Primary signal.</summary>
    public double ToolCoverage { get; set; } = 0.4;

    /// <summary>Weight for SubagentType alignment with task category (default 0.3).</summary>
    public double TypeAlignment { get; set; } = 0.3;

    /// <summary>Weight for tier headroom above minimum required (default 0.3).</summary>
    public double TierHeadroom { get; set; } = 0.3;
}
