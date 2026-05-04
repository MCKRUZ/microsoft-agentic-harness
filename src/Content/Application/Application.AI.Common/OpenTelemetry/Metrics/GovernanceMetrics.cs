using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking governance policy decisions, violations,
/// prompt injection detections, and MCP security scans.
/// </summary>
public static class GovernanceMetrics
{
    /// <summary>Total policy decisions. Tags: agent.governance.action, agent.governance.tool.</summary>
    public static Counter<long> Decisions { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.Decisions, "{decision}", "Governance policy decisions");

    /// <summary>Policy violations (denied actions). Tags: agent.governance.policy, agent.governance.rule.</summary>
    public static Counter<long> Violations { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.Violations, "{violation}", "Governance policy violations");

    /// <summary>Policy evaluation latency in milliseconds.</summary>
    public static Histogram<double> EvaluationDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(GovernanceConventions.EvaluationDuration, "ms", "Governance evaluation duration");

    /// <summary>Rate limit hits. Tags: agent.governance.tool.</summary>
    public static Counter<long> RateLimitHits { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.RateLimitHits, "{hit}", "Governance rate limit hits");

    /// <summary>Audit events emitted. Tags: agent.governance.action.</summary>
    public static Counter<long> AuditEvents { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.AuditEvents, "{event}", "Governance audit events");

    /// <summary>Prompt injection detections. Tags: agent.safety.category.</summary>
    public static Counter<long> InjectionDetections { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.InjectionDetections, "{detection}", "Prompt injection detections");

    /// <summary>MCP tool security scans performed.</summary>
    public static Counter<long> McpScans { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.McpScans, "{scan}", "MCP tool security scans");

    /// <summary>MCP tool threats detected.</summary>
    public static Counter<long> McpThreats { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.McpThreats, "{threat}", "MCP tool threats detected");
}
