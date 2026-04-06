using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking context window / token budget utilization
/// and compaction events per agent.
/// </summary>
/// <remarks>
/// Budget utilization gauge is registered separately via callback in the configurator
/// because it requires access to the budget tracker service.
/// </remarks>
public static class ContextBudgetMetrics
{
    /// <summary>Compaction event count. Tags: agent.name, agent.context.compaction_reason.</summary>
    public static Counter<long> Compactions { get; } =
        AppInstrument.Meter.CreateCounter<long>(ContextConventions.Compactions, "{compaction}", "Context compaction events");
}
