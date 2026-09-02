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
    public const string AuditWriteFailures = "agent.governance.audit_write_failures";
    public const string InjectionDetections = "agent.governance.injection_detections";
    public const string McpScans = "agent.governance.mcp_scans";
    public const string McpThreats = "agent.governance.mcp_threats";
    public const string McpToolsWithheld = "agent.governance.mcp_tools_withheld";
    public const string McpToolCollisions = "agent.governance.mcp_tool_collisions";
    public const string McpToolShadowing = "agent.governance.mcp_tool_shadowing";
    public const string McpToolDrift = "agent.governance.mcp_tool_drift";

    /// <summary>
    /// Highest severity found on a withheld MCP tool. Bounded by the <c>ThreatLevel</c> enum, unlike
    /// the tool name, which an untrusted server controls and which therefore stays out of tag space.
    /// </summary>
    public const string McpThreatSeverityTag = "agent.governance.mcp.severity";

    public const string ResponseSanitizations = "agent.governance.response.sanitizations";
    public const string ResponseBlocks = "agent.governance.response.blocks";
    public const string SanitizationDuration = "agent.governance.response.sanitization_duration";
    public const string SanitizationCategoryTag = "agent.governance.sanitization.category";

    /// <summary>
    /// A sanitizer in the composite chain that was skipped because its own regex match timed out. The
    /// skipped pass contributes no <see cref="ResponseSanitizations"/> of its own, so without this
    /// counter a hostile input that reliably times one rule out is indistinguishable on the metrics
    /// from content that was simply clean — the one shape an attacker would deliberately produce.
    /// </summary>
    public const string SanitizerTimeouts = "agent.governance.response.sanitizer_timeouts";

    public const string AuditChainVerifications = "agent.governance.audit_chain.verifications";
    public const string AuditChainBreaks = "agent.governance.audit_chain.breaks";
    public const string AuditChainNameTag = "agent.governance.audit_chain.name";

    public const string SpinInterventions = "agent.governance.spin_interventions";
    public const string SpinReasonTag = "agent.governance.spin.reason";
    public const string SpinModeTag = "agent.governance.spin.mode";

    public const string ClassificationDecisions = "agent.governance.classification_decisions";
    public const string ClassificationActionTag = "agent.governance.classification.action";
    public const string ClassificationAssetTypeTag = "agent.governance.classification.asset_type";
    public const string ClassificationLabelSourceTag = "agent.governance.classification.label_source";
    public const string ClassificationModeTag = "agent.governance.classification.mode";
    public const string EnforcedTag = "agent.governance.enforced";

    /// <summary>
    /// Tool-composition findings emitted at agent build time — a source-capable tool co-resident with a
    /// sink-capable tool under a posture that is not Allow. Tags: <see cref="CompositionSourceCapabilityTag"/>,
    /// <see cref="CompositionSinkCapabilityTag"/>, <see cref="CompositionPostureTag"/>. Deliberately
    /// carries no tool-name or agent-name tag — both are attacker-influenced strings of unbounded
    /// cardinality, the same rule <see cref="McpToolCollisions"/> already applies.
    /// </summary>
    public const string ToolCompositionFindings = "agent.governance.tool_composition_findings";

    /// <summary>
    /// Tools an agent's composition analysis could not classify as either a source or a sink. Untagged —
    /// this is a coverage metric, not a per-tool one. A rising count relative to tool-set size is the
    /// signal that the check's blind spot (see <c>ToolCapabilityProfile.Unclassified</c>) is growing.
    /// </summary>
    public const string ToolCompositionUnclassified = "agent.governance.tool_composition_unclassified";

    public const string CompositionSourceCapabilityTag = "agent.governance.composition.source_capability";
    public const string CompositionSinkCapabilityTag = "agent.governance.composition.sink_capability";
    public const string CompositionPostureTag = "agent.governance.composition.posture";

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
