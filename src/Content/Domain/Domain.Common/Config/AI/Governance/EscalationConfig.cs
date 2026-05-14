namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Root configuration for the human escalation subsystem.
/// Bound from <c>AppConfig:AI:Governance:Escalation</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI.Governance.Escalation
/// ├── Enabled                  — Master toggle for escalation
/// ├── DefaultTimeoutSeconds    — Global escalation timeout
/// ├── DefaultTimeoutAction     — Deny / DenyAndEscalate / Approve / Escalate
/// ├── DefaultApprovalStrategy  — AnyOf / AllOf / Quorum
/// ├── AuditStoragePath          — Directory for JSONL audit log
/// └── PriorityLevels{}         — Per-priority overrides keyed by EscalationPriority name
///     ├── TimeoutSeconds       — Override timeout for this level
///     ├── Async                — Non-blocking mode (informational)
///     └── EscalateToAll        — Notify all approvers simultaneously (critical)
/// </code>
/// </para>
/// </remarks>
public class EscalationConfig
{
    /// <summary>
    /// Whether the escalation system is active. When disabled,
    /// <c>GovernancePolicyBehavior</c> treats <c>RequireApproval</c> as a denial.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How long (in seconds) to wait for approver responses before firing the timeout action.
    /// Zero is valid for informational-only escalations.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Action taken when escalation times out. String value of <c>EscalationTimeoutAction</c>
    /// enum: "Deny", "DenyAndEscalate", "Approve", "Escalate".
    /// Validated at the Application layer.
    /// </summary>
    public string DefaultTimeoutAction { get; set; } = "DenyAndEscalate";

    /// <summary>
    /// Default approval strategy when a governance rule does not specify one.
    /// String value of <c>ApprovalStrategyType</c> enum: "AnyOf", "AllOf", "Quorum".
    /// Validated at the Application layer.
    /// </summary>
    public string DefaultApprovalStrategy { get; set; } = "AnyOf";

    /// <summary>
    /// Per-priority-level overrides keyed by <c>EscalationPriority</c> name
    /// ("Informational", "Blocking", "Critical").
    /// </summary>
    public Dictionary<string, EscalationPriorityConfig> PriorityLevels { get; set; } = new();

    /// <summary>
    /// Directory path for the JSONL escalation audit store.
    /// Relative paths are resolved from the application working directory.
    /// </summary>
    public string AuditStoragePath { get; set; } = ".agent-sessions/escalations";
}
