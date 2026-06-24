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

    public const string ResponseSanitizations = "agent.governance.response.sanitizations";
    public const string ResponseBlocks = "agent.governance.response.blocks";
    public const string SanitizationDuration = "agent.governance.response.sanitization_duration";
    public const string SanitizationCategoryTag = "agent.governance.sanitization.category";

    public const string AuditChainVerifications = "agent.governance.audit_chain.verifications";
    public const string AuditChainBreaks = "agent.governance.audit_chain.breaks";
    public const string AuditChainNameTag = "agent.governance.audit_chain.name";

    public const string SpinInterventions = "agent.governance.spin_interventions";
    public const string SpinReasonTag = "agent.governance.spin.reason";
    public const string SpinModeTag = "agent.governance.spin.mode";

    /// <summary>Tag values for <see cref="SpinReasonTag"/> — why the spin guard broke the loop.</summary>
    public static class SpinReasonValues
    {
        /// <summary>The identical call (same tool + arguments) fired consecutively past the threshold.</summary>
        public const string Repetition = "repetition";

        /// <summary>A window of calls introduced no new call signature — no progress.</summary>
        public const string NoProgress = "no_progress";
    }

    /// <summary>Tag values for <see cref="SpinModeTag"/> — what the guard did on a detected spin.</summary>
    public static class SpinModeValues
    {
        /// <summary>The loop was broken locally with a model-facing message; no escalation raised.</summary>
        public const string Stop = "stop";

        /// <summary>The loop was broken and an escalation reason code was raised on the governance trace.</summary>
        public const string Escalate = "escalate";
    }

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
