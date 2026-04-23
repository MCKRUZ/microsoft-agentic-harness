using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking composite tool usefulness scores.
/// Scores range 0-1 based on result quality, chain detection, reference tracking,
/// and substance analysis.
/// </summary>
public static class ToolUsefulnessMetrics
{
    /// <summary>Composite usefulness score (0-1). Tags: agent.tool.name, agent.name.</summary>
    public static Histogram<double> UsefulnessScore { get; } =
        AppInstrument.Meter.CreateHistogram<double>(ToolConventions.UsefulnessScore, "{ratio}", "Tool usefulness score (0-1)");
}
