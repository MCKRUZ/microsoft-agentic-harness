using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking content safety filter outcomes — pass/block/redact
/// rates per filter type, phase, and category. Enables compliance dashboards.
/// </summary>
public static class ContentSafetyMetrics
{
    /// <summary>Total evaluations per filter. Tags: agent.safety.phase, agent.safety.filter, agent.safety.outcome.</summary>
    public static Counter<long> Evaluations { get; } =
        AppInstrument.Meter.CreateCounter<long>(SafetyConventions.Evaluations, "{evaluation}", "Content safety evaluations");

    /// <summary>Block count with category detail. Tags: agent.safety.phase, agent.safety.filter, agent.safety.category.</summary>
    public static Counter<long> Blocks { get; } =
        AppInstrument.Meter.CreateCounter<long>(SafetyConventions.Blocks, "{block}", "Content safety blocks");

    /// <summary>Severity distribution. Tags: agent.safety.phase, agent.safety.category.</summary>
    public static Histogram<int> Severity { get; } =
        AppInstrument.Meter.CreateHistogram<int>(SafetyConventions.Severity, "{level}", "Content safety severity distribution");
}
