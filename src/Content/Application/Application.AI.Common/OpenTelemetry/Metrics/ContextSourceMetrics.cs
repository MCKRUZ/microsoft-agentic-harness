using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking context window token distribution
/// by source type. Enables the Context Explorer dashboard to visualize
/// how different sources (system prompt, skills, tools, hooks, messages)
/// consume the context budget.
/// </summary>
public static class ContextSourceMetrics
{
    /// <summary>Token count by context source type. Tags: agent.context.source_type, agent.name.</summary>
    public static Histogram<long> SourceTokens { get; } =
        AppInstrument.Meter.CreateHistogram<long>(ContextConventions.SourceTokens, "{token}", "Token count by context source type");
}
