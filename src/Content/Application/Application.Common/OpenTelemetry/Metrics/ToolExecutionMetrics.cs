using System.Diagnostics.Metrics;
using Application.Common.OpenTelemetry.Conventions;
using Application.Common.OpenTelemetry.Instruments;

namespace Application.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking tool invocation duration, count, and error rate.
/// The harness is fundamentally a tool dispatch loop — tool health is the primary operational signal.
/// </summary>
public static class ToolExecutionMetrics
{
    /// <summary>Execution duration per tool. Tags: agent.tool.name, agent.tool.source, agent.tool.status.</summary>
    public static Histogram<double> Duration { get; } =
        AgenticHarnessInstrument.Meter.CreateHistogram<double>(AgenticSemanticConventions.Tool.Duration, "ms", "Tool execution duration");

    /// <summary>Total invocations per tool. Tags: agent.tool.name, agent.tool.source, agent.tool.status.</summary>
    public static Counter<long> Invocations { get; } =
        AgenticHarnessInstrument.Meter.CreateCounter<long>(AgenticSemanticConventions.Tool.Invocations, "{invocation}", "Total tool invocations");

    /// <summary>Error count by type. Tags: agent.tool.name, agent.tool.source, agent.tool.error_type.</summary>
    public static Counter<long> Errors { get; } =
        AgenticHarnessInstrument.Meter.CreateCounter<long>(AgenticSemanticConventions.Tool.Errors, "{error}", "Tool execution errors");
}
