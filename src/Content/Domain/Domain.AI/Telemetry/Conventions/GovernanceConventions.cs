namespace Domain.AI.Telemetry.Conventions;

/// <summary>Governance telemetry attribute names and metric identifiers.</summary>
public static class GovernanceConventions
{
    public const string PolicyName = "agent.governance.policy";
    public const string RuleName = "agent.governance.rule";
    public const string Action = "agent.governance.action";
    public const string Scope = "agent.governance.scope";
    public const string ToolName = "agent.governance.tool";

    public const string Decisions = "agent.governance.decisions";
    public const string Violations = "agent.governance.violations";
    public const string EvaluationDuration = "agent.governance.evaluation_duration";
    public const string RateLimitHits = "agent.governance.rate_limit_hits";
    public const string AuditEvents = "agent.governance.audit_events";
    public const string InjectionDetections = "agent.governance.injection_detections";
    public const string McpScans = "agent.governance.mcp_scans";
    public const string McpThreats = "agent.governance.mcp_threats";

    public static class ActionValues
    {
        public const string Allow = "allow";
        public const string Deny = "deny";
        public const string Warn = "warn";
        public const string RequireApproval = "require_approval";
        public const string RateLimit = "rate_limit";
    }

    public static class ScopeValues
    {
        public const string Global = "global";
        public const string Tenant = "tenant";
        public const string Organization = "organization";
        public const string Agent = "agent";
    }
}
