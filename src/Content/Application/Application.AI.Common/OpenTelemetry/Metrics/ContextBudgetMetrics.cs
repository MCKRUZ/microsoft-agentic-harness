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

    // Nexus-inspired context overhead gauges — recorded via observable callbacks
    // registered in the telemetry configurator with access to the budget tracker service.

    /// <summary>System prompt token load. Tags: agent.name.</summary>
    public static Histogram<long> SystemPromptTokens { get; } =
        AppInstrument.Meter.CreateHistogram<long>(ContextConventions.SystemPromptTokens, "{token}", "System prompt token load");

    /// <summary>Skills token load by tier. Tags: agent.name, agent.context.skills_tier.</summary>
    public static Histogram<long> SkillsLoadedTokens { get; } =
        AppInstrument.Meter.CreateHistogram<long>(ContextConventions.SkillsLoadedTokens, "{token}", "Skills loaded token load");

    /// <summary>Tool schemas token load. Tags: agent.name.</summary>
    public static Histogram<long> ToolsSchemaTokens { get; } =
        AppInstrument.Meter.CreateHistogram<long>(ContextConventions.ToolsSchemaTokens, "{token}", "Tool schemas token load");

    /// <summary>Budget utilization ratio (0-1). Tags: agent.name.</summary>
    public static Histogram<double> BudgetUtilization { get; } =
        AppInstrument.Meter.CreateHistogram<double>(ContextConventions.BudgetUtilization, "{ratio}", "Context budget utilization ratio");
}
