namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// OTel attribute names and metric identifiers for the escalation subsystem.
/// Used throughout Phase 2 to tag spans, structured logs, and metric recordings
/// with consistent, discoverable dimension keys.
/// </summary>
public static class EscalationConventions
{
    // ── Attribute name constants (span/log attribute keys) ──

    /// <summary>Unique escalation identifier for cross-service correlation.</summary>
    public const string EscalationId = "agent.escalation.id";

    /// <summary>The agent that triggered the escalation request.</summary>
    public const string AgentId = "agent.escalation.agent_id";

    /// <summary>The tool invocation that required escalation approval.</summary>
    public const string ToolName = "agent.escalation.tool";

    /// <summary>Escalation priority level (informational, blocking, critical).</summary>
    public const string Priority = "agent.escalation.priority";

    /// <summary>How the escalation was resolved (approved, denied, timed_out, escalated).</summary>
    public const string ResolutionType = "agent.escalation.resolution_type";

    /// <summary>Approval strategy used (any_of, all_of, quorum).</summary>
    public const string Strategy = "agent.escalation.strategy";

    /// <summary>Individual approver identifier for per-approver tracking.</summary>
    public const string ApproverName = "agent.escalation.approver";

    // ── Metric identifier constants (instrument names) ──

    /// <summary>Counter of escalation requests created. Tags: agent_id, tool, priority.</summary>
    public const string Requests = "agent.escalation.requests";

    /// <summary>Counter of resolved escalations. Tags: resolution_type, priority.</summary>
    public const string Resolutions = "agent.escalation.resolutions";

    /// <summary>Histogram of time from escalation request to resolution, in milliseconds.</summary>
    public const string DurationMs = "agent.escalation.duration_ms";

    /// <summary>Counter of escalations that exceeded their timeout window.</summary>
    public const string Timeouts = "agent.escalation.timeouts";

    /// <summary>Gauge of currently active pending escalations (inc on request, dec on resolution).</summary>
    public const string Pending = "agent.escalation.pending";

    /// <summary>Histogram of individual approver response latency in milliseconds. Tags: approver.</summary>
    public const string ApproverResponseMs = "agent.escalation.approver_response_ms";

    // ── Well-known tag value classes ──

    /// <summary>Well-known values for the <see cref="Priority"/> attribute.</summary>
    public static class PriorityValues
    {
        public const string Informational = "informational";
        public const string Blocking = "blocking";
        public const string Critical = "critical";
    }

    /// <summary>Well-known values for the <see cref="ResolutionType"/> attribute.</summary>
    public static class ResolutionValues
    {
        public const string Approved = "approved";
        public const string Denied = "denied";
        public const string TimedOut = "timed_out";
        public const string Escalated = "escalated";
    }

    /// <summary>Well-known values for the <see cref="Strategy"/> attribute.</summary>
    public static class StrategyValues
    {
        public const string AnyOf = "any_of";
        public const string AllOf = "all_of";
        public const string Quorum = "quorum";
    }
}
