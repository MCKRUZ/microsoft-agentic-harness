using System.Diagnostics.Metrics;
using Application.Common.OpenTelemetry.Conventions;
using Application.Common.OpenTelemetry.Instruments;

namespace Application.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking content safety filter outcomes — pass/block/redact
/// rates per filter type, phase, and category. Enables compliance dashboards.
/// </summary>
public static class ContentSafetyMetrics
{
    /// <summary>Total evaluations per filter. Tags: agent.safety.phase, agent.safety.filter, agent.safety.outcome.</summary>
    public static Counter<long> Evaluations { get; } =
        AgenticHarnessInstrument.Meter.CreateCounter<long>(AgenticSemanticConventions.Safety.Evaluations, "{evaluation}", "Content safety evaluations");

    /// <summary>Block count with category detail. Tags: agent.safety.phase, agent.safety.filter, agent.safety.category.</summary>
    public static Counter<long> Blocks { get; } =
        AgenticHarnessInstrument.Meter.CreateCounter<long>(AgenticSemanticConventions.Safety.Blocks, "{block}", "Content safety blocks");

    /// <summary>Severity distribution. Tags: agent.safety.phase, agent.safety.category.</summary>
    public static Histogram<int> Severity { get; } =
        AgenticHarnessInstrument.Meter.CreateHistogram<int>(AgenticSemanticConventions.Safety.Severity, "{level}", "Content safety severity distribution");
}
