using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking tool invocation duration, count, and error rate.
/// The harness is fundamentally a tool dispatch loop — tool health is the primary operational signal.
/// </summary>
public static class ToolExecutionMetrics
{
    /// <summary>Execution duration per tool. Tags: agent.tool.name, agent.tool.source, agent.tool.status.</summary>
    public static Histogram<double> Duration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(ToolConventions.Duration, "ms", "Tool execution duration");

    /// <summary>Total invocations per tool. Tags: agent.tool.name, agent.tool.source, agent.tool.status.</summary>
    public static Counter<long> Invocations { get; } =
        AppInstrument.Meter.CreateCounter<long>(ToolConventions.Invocations, "{invocation}", "Total tool invocations");

    /// <summary>Error count by type. Tags: agent.tool.name, agent.tool.source, agent.tool.error_type.</summary>
    public static Counter<long> Errors { get; } =
        AppInstrument.Meter.CreateCounter<long>(ToolConventions.Errors, "{error}", "Tool execution errors");
}
