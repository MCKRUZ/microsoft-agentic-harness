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

    /// <summary>
    /// Governance audit writes that failed to append (disk full, permission denied, broken
    /// <c>AuditStoragePath</c>, etc). The writer never throws on a write failure — the tool-call
    /// decision it's recording must never fail because of it — so this counter is the only
    /// non-log-line signal a write failure ever produces. Tags: agent.governance.action.
    /// </summary>
    public static Counter<long> AuditWriteFailures { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.AuditWriteFailures, "{failure}", "Governance audit writes that failed to append");

    /// <summary>Prompt injection detections. Tags: agent.safety.category.</summary>
    public static Counter<long> InjectionDetections { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.InjectionDetections, "{detection}", "Prompt injection detections");

    /// <summary>
    /// Data-classification gate decisions on the live tool path. Tags: agent.governance.tool,
    /// agent.governance.classification.action, agent.governance.classification.asset_type,
    /// agent.governance.classification.label_source, agent.governance.classification.mode,
    /// agent.governance.enforced.
    /// </summary>
    public static Counter<long> ClassificationDecisions { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.ClassificationDecisions, "{decision}", "Data-classification gate decisions");

    /// <summary>MCP tool security scans performed.</summary>
    public static Counter<long> McpScans { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.McpScans, "{scan}", "MCP tool security scans");

    /// <summary>MCP tool threats detected.</summary>
    public static Counter<long> McpThreats { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.McpThreats, "{threat}", "MCP tool threats detected");

    /// <summary>
    /// MCP tools withheld from the model because a scan finding met the configured block threshold.
    /// Tags: agent.governance.mcp.severity. Deliberately not tagged with the tool name — that string
    /// comes from an untrusted server and would put unbounded cardinality in the metric backend.
    /// </summary>
    public static Counter<long> McpToolsWithheld { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.McpToolsWithheld, "{tool}", "MCP tools withheld after a security scan");

    /// <summary>
    /// Tool name collisions found on the aggregated MCP surface — two servers advertising a tool
    /// under the same normalised name. Deliberately untagged with the tool or server name; both come
    /// from an untrusted server.
    /// </summary>
    public static Counter<long> McpToolCollisions { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.McpToolCollisions, "{collision}", "MCP tool name collisions detected across servers");

    /// <summary>
    /// Cross-server shadowing findings — a tool's description references another server's tool by
    /// name.
    /// </summary>
    public static Counter<long> McpToolShadowing { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.McpToolShadowing, "{finding}", "MCP cross-server tool shadowing findings");

    /// <summary>
    /// Definition drift (rug pull) findings — a previously-seen tool's description or schema hash
    /// changed.
    /// </summary>
    public static Counter<long> McpToolDrift { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.McpToolDrift, "{finding}", "MCP tool definition drift findings");

    /// <summary>Response sanitization actions taken. Tags: agent.governance.sanitization.category, agent.governance.tool.</summary>
    public static Counter<long> ResponseSanitizations { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.ResponseSanitizations, "{sanitization}", "Response sanitization actions");

    /// <summary>Responses blocked due to threat level exceeding threshold. Tags: agent.governance.tool.</summary>
    public static Counter<long> ResponseBlocks { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.ResponseBlocks, "{block}", "Response blocks due to high threat level");

    /// <summary>Response sanitization latency in milliseconds.</summary>
    public static Histogram<double> SanitizationDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(GovernanceConventions.SanitizationDuration, "ms", "Response sanitization duration");

    /// <summary>
    /// Sanitizer passes skipped because the sanitizer's own regex match timed out. Tags:
    /// agent.governance.sanitization.category, agent.governance.tool. See
    /// <see cref="GovernanceConventions.SanitizerTimeouts"/> for why a skipped pass needs a signal of
    /// its own rather than being inferred from the absence of sanitizations.
    /// </summary>
    public static Counter<long> SanitizerTimeouts { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.SanitizerTimeouts, "{timeout}", "Sanitizer passes skipped after a regex match timeout");

    /// <summary>Audit-chain integrity verifications performed. Tags: agent.governance.audit_chain.name.</summary>
    public static Counter<long> AuditChainVerifications { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.AuditChainVerifications, "{verification}", "Audit-chain integrity verifications");

    /// <summary>Audit-chain integrity breaks detected (tampering, deletion, or corruption). Tags: agent.governance.audit_chain.name.</summary>
    public static Counter<long> AuditChainBreaks { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.AuditChainBreaks, "{break}", "Audit-chain integrity breaks detected");

    /// <summary>
    /// Spin / no-progress guard interventions — the agent loop was broken because it was repeating an
    /// identical call or making no progress. Tags: agent.governance.spin.reason (repetition |
    /// no_progress), agent.governance.spin.mode (stop | escalate), agent.governance.tool.
    /// </summary>
    public static Counter<long> SpinInterventions { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.SpinInterventions, "{intervention}", "Spin / no-progress guard interventions");

    /// <summary>
    /// Tool-composition findings — an untrusted-input or credential-reading tool co-resident with a
    /// file-write, code-execution, or outbound-send tool, under a posture that is not Allow. Tags:
    /// agent.governance.composition.source_capability, agent.governance.composition.sink_capability,
    /// agent.governance.composition.posture. No tool-name tag — see <see cref="McpToolCollisions"/>'s
    /// remarks for the same cardinality rule.
    /// </summary>
    public static Counter<long> ToolCompositionFindings { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.ToolCompositionFindings, "{finding}", "Tool composition findings");

    /// <summary>
    /// Tools a composition analysis could not classify as either a source or a sink — the coverage gap
    /// this check's deliberate fail-open produces, made visible rather than silent. Untagged.
    /// </summary>
    public static Counter<long> ToolCompositionUnclassified { get; } =
        AppInstrument.Meter.CreateCounter<long>(GovernanceConventions.ToolCompositionUnclassified, "{tool}", "Tools unclassified by composition analysis");
}
